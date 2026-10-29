using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IceyCoop.Transport
{
    /// <summary>
    /// Minimal TCP room relay (plan T23).
    ///
    /// Peer A and peer B each connect, announce the same six-byte room code, and
    /// from then on the relay copies bytes in both directions. It never parses the
    /// game protocol: the host's own transport keeps doing framing, ordering and
    /// security, so the relay cannot corrupt or misinterpret a frame.
    ///
    /// Blocking sockets and dedicated threads only - the same constraint the rest
    /// of this project has on the old Mono runtime (no async/await, no
    /// Socket.Select/Poll tricks).
    /// </summary>
    public sealed class RelayServer : IDisposable
    {
        /// <summary>How long a lone peer may wait for its partner.</summary>
        private const uint LonePeerTimeoutMs = 15000;

        private const int CodeBytes = RoomRegistry.RoomCodeLength;

        private readonly RoomRegistry rooms;
        private readonly Dictionary<string, PendingPeer> pending = new Dictionary<string, PendingPeer>();
        private readonly List<TcpClient> live = new List<TcpClient>();
        private readonly object gate = new object();

        private TcpListener listener;
        private Thread acceptThread;
        private Thread janitorThread;
        private volatile bool stopping;
        private int port;
        private int pairedSessions;
        private long bytesFromFirstPeer;
        private long bytesFromSecondPeer;

        private sealed class PendingPeer
        {
            public TcpClient Client;
            public string Code;
            public uint JoinedAtMs;
        }

        public RelayServer()
            : this(new RoomRegistry())
        {
        }

        public RelayServer(RoomRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException("registry");
            }
            rooms = registry;
        }

        public int Port
        {
            get { return port; }
        }

        public RoomRegistry Rooms
        {
            get { return rooms; }
        }

        /// <summary>Number of two-peer sessions established since start.</summary>
        public int PairedSessions
        {
            get { return pairedSessions; }
        }

        /// <summary>Bytes copied from the peer that joined first to the one that joined second.</summary>
        public long BytesFromFirstPeer { get { return System.Threading.Interlocked.Read(ref bytesFromFirstPeer); } }

        /// <summary>Bytes copied from the peer that joined second back to the first.</summary>
        public long BytesFromSecondPeer { get { return System.Threading.Interlocked.Read(ref bytesFromSecondPeer); } }

        /// <summary>Starts listening. Pass 0 to bind an ephemeral port (tests).</summary>
        public void Start(int listenPort)
        {
            if (listener != null)
            {
                throw new InvalidOperationException("relay already started");
            }
            listener = new TcpListener(IPAddress.Any, listenPort);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            stopping = false;

            acceptThread = new Thread(new ThreadStart(AcceptLoop));
            acceptThread.IsBackground = true;
            acceptThread.Name = "iceycoop-relay-accept";
            acceptThread.Start();

            janitorThread = new Thread(new ThreadStart(JanitorLoop));
            janitorThread.IsBackground = true;
            janitorThread.Name = "iceycoop-relay-janitor";
            janitorThread.Start();
        }

        public void Stop()
        {
            stopping = true;
            try
            {
                if (listener != null)
                {
                    listener.Stop();
                }
            }
            catch
            {
                // stopping is best effort
            }
            lock (gate)
            {
                foreach (KeyValuePair<string, PendingPeer> kv in pending)
                {
                    CloseQuiet(kv.Value.Client);
                }
                pending.Clear();
                for (int i = 0; i < live.Count; i++)
                {
                    CloseQuiet(live[i]);
                }
                live.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
            listener = null;
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    if (stopping)
                    {
                        return;
                    }
                    continue;
                }
                try
                {
                    HandleIncoming(client);
                }
                catch
                {
                    CloseQuiet(client);
                }
            }
        }

        private void HandleIncoming(TcpClient client)
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();
            byte[] codeBuffer = new byte[CodeBytes];
            if (!ReadExactly(stream, codeBuffer, CodeBytes))
            {
                CloseQuiet(client);
                return;
            }
            string code = Encoding.ASCII.GetString(codeBuffer);
            if (!RoomRegistry.IsWellFormed(code))
            {
                CloseQuiet(client);
                return;
            }
            // ReadExactly armed a five second receive timeout so a stranger that connects and says
            // nothing cannot sit on the accept thread. That timeout lives on the socket, and this
            // very socket is what Pump keeps reading from for the rest of the session - leaving it
            // on kills a healthy pair the moment both sides go quiet for five seconds, which is
            // exactly what a scene load looks like (one side loading, the other waiting for
            // SceneReady). Clear it before the pair starts.
            try { stream.ReadTimeout = 0; }
            catch { }

            PendingPeer first;
            lock (gate)
            {
                if (pending.TryGetValue(code, out first))
                {
                    pending.Remove(code);
                    live.Add(first.Client);
                    live.Add(client);
                }
                else
                {
                    PendingPeer slot = new PendingPeer();
                    slot.Client = client;
                    slot.Code = code;
                    slot.JoinedAtMs = NowMs();
                    pending[code] = slot;
                    return;
                }
            }

            // Second peer arrived: pair and start pumping.
            pairedSessions++;
            CloseQuiet(null);
            Thread pumpA = StartPump(first.Client, client, true);
            Thread pumpB = StartPump(client, first.Client, false);
            pumpA.Start();
            pumpB.Start();
        }

        private Thread StartPump(TcpClient from, TcpClient to, bool firstDirection)
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                Pump(from, to, firstDirection);
            }));
            thread.IsBackground = true;
            thread.Name = "iceycoop-relay-pump";
            return thread;
        }

        private void Pump(TcpClient from, TcpClient to, bool firstDirection)
        {
            byte[] buffer = new byte[16 * 1024];
            try
            {
                NetworkStream inStream = from.GetStream();
                NetworkStream outStream = to.GetStream();
                while (true)
                {
                    int read;
                    try
                    {
                        read = inStream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception readError)
                    {
                        // A quiet wire is not a dead pair. A scene load leaves one side busy and the
                        // other waiting for SceneReady, so five seconds of silence is normal traffic
                        // - it must never be mistaken for a disconnect (that mistake killed every
                        // relay session the moment the client loaded a scene).
                        if (IsTimeout(readError))
                        {
                            continue;
                        }
                        throw;
                    }
                    if (read <= 0)
                    {
                        break;
                    }
                    outStream.Write(buffer, 0, read);
                    outStream.Flush();
                    if (firstDirection) { System.Threading.Interlocked.Add(ref bytesFromFirstPeer, read); }
                    else { System.Threading.Interlocked.Add(ref bytesFromSecondPeer, read); }
                }
            }
            catch
            {
                // either side going away ends the session
            }
            CloseQuiet(from);
            CloseQuiet(to);
        }

        private static bool IsTimeout(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                SocketException socket = current as SocketException;
                if (socket != null && socket.SocketErrorCode == SocketError.TimedOut) return true;
                if (current is TimeoutException) return true;
            }
            return false;
        }

        private void JanitorLoop()
        {
            while (!stopping)
            {
                try
                {
                    Thread.Sleep(1000);
                }
                catch
                {
                    return;
                }
                uint now = NowMs();
                List<TcpClient> expired = new List<TcpClient>();
                lock (gate)
                {
                    List<string> dead = new List<string>();
                    foreach (KeyValuePair<string, PendingPeer> kv in pending)
                    {
                        if (now - kv.Value.JoinedAtMs > LonePeerTimeoutMs)
                        {
                            dead.Add(kv.Key);
                            expired.Add(kv.Value.Client);
                        }
                    }
                    for (int i = 0; i < dead.Count; i++)
                    {
                        pending.Remove(dead[i]);
                    }
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    CloseQuiet(expired[i]);
                }
                rooms.PurgeExpired(now);
            }
        }

        private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            try
            {
                stream.ReadTimeout = 5000;
            }
            catch
            {
                // timeout is advisory
            }
            while (offset < count)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, offset, count - offset);
                }
                catch
                {
                    return false;
                }
                if (read <= 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }

        private static void CloseQuiet(TcpClient client)
        {
            if (client == null)
            {
                return;
            }
            try
            {
                client.Close();
            }
            catch
            {
                // closing is best effort
            }
        }

        private static uint NowMs()
        {
            return (uint)(Environment.TickCount & 0x7FFFFFFF);
        }
    }
}
