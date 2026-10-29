using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IceyCoop.Transport
{
    /// <summary>
    /// T23 relay link: both ends dial the same room on a <see cref="RelayServer"/> and then speak
    /// the ordinary co-op protocol over that one socket. The relay never parses a frame - it copies
    /// bytes between the two peers - so framing, ordering and authentication stay exactly where they
    /// were; this class only replaces "listen on a port / reach the host's address" with "reach a
    /// rendezvous point both sides can route to". That is what makes a session work without port
    /// forwarding.
    ///
    /// Two consequences worth knowing:
    ///  - There is no UDP path. Unreliable frames travel on the same stream, carrying a channel tag
    ///    so the receiver still sees them as unreliable. That is a legal degradation of
    ///    <see cref="INetworkLink"/> (unreliable frames *may* be lost, they need not be) and costs
    ///    latency on snapshots rather than correctness.
    ///  - The link is symmetric: both roles call <see cref="StartHost"/> or <see cref="Connect"/>,
    ///    and both dial the relay. <see cref="IsConnected"/> therefore means "the rendezvous socket is
    ///    up", not "a peer has already said hello"; the session layer's own Hello/Welcome handshake is
    ///    what establishes the peer, and the relay buffers the first peer's bytes until it pairs.
    ///
    /// Blocking sockets and dedicated threads only (old Mono constraint, same as TcpUdpLink).
    /// </summary>
    public sealed class RelayLink : INetworkLink
    {
        private const byte ChannelReliable = 1;
        private const byte ChannelUnreliable = 0;
        private const int MaxFrameBytes = 64 * 1024;
        private const int RedialDelayMs = 200;

        /// <summary>
        /// Both channels share one socket here, so the queue carries the snapshot stream as well.
        /// A scene load blocks the game's main thread for seconds while snapshots keep arriving, and
        /// the direct link survives that only because its snapshots ride a separate UDP socket. The
        /// relay budgets are therefore larger than TcpUdpLink's, and an overflow drops stale
        /// snapshots instead of the connection (see <see cref="TrimUnreliableLocked"/>).
        /// </summary>
        public const int DefaultMaxQueuedFrames = 8192;

        public const int DefaultMaxQueuedBytes = 1024 * 1024;

        private readonly string relayHost;
        private readonly int relayPort;
        private readonly string roomCode;
        private readonly int maxQueuedFrames;
        private readonly int maxQueuedBytes;
        private readonly object gate = new object();
        private readonly Queue<byte[]> reliableOut = new Queue<byte[]>();
        private readonly Queue<byte[]> unreliableOut = new Queue<byte[]>();
        private readonly Queue<InboundFrame> incoming = new Queue<InboundFrame>();

        private Socket socket;
        private Thread receiveThread;
        private Thread sendThread;
        private Thread redialThread;
        private bool running;
        private bool overflowed;
        private bool hostRole;
        private bool peerHeard;
        private readonly bool diagnose;
        private int pollCount;
        private int framesReceived;
        private long queuedBytes;
        private int queuedFrames;

        private struct InboundFrame
        {
            public byte[] Payload;
            public bool Reliable;
        }

        public RelayLink(string relayHost, int relayPort, string roomCode)
            : this(relayHost, relayPort, roomCode, DefaultMaxQueuedFrames, DefaultMaxQueuedBytes)
        {
        }

        public RelayLink(string relayHost, int relayPort, string roomCode, int maxQueuedFrames, int maxQueuedBytes)
        {
            if (string.IsNullOrEmpty(relayHost)) throw new ArgumentException("relayHost");
            if (relayPort <= 0 || relayPort > 65535) throw new ArgumentOutOfRangeException("relayPort");
            if (!RoomRegistry.IsWellFormed(roomCode))
            {
                throw new ArgumentException("room code must be six ASCII letters or digits: " + roomCode);
            }
            this.relayHost = relayHost;
            this.relayPort = relayPort;
            this.roomCode = roomCode;
            this.maxQueuedFrames = maxQueuedFrames;
            this.maxQueuedBytes = maxQueuedBytes;
            this.diagnose = Environment.GetEnvironmentVariable("ICEYCOOP_RELAY_DIAG") == "1";
        }

        public event Action<byte[], bool> OnFrame;
        public event Action OnDisconnected;
        public event Action<string> OnDiagnostic;

        public string RoomCode { get { return roomCode; } }

        /// <summary>Frames queued for the relay but not yet written.</summary>
        public int QueuedFrames
        {
            get { lock (gate) { return reliableOut.Count + unreliableOut.Count; } }
        }

        /// <summary>
        /// True once this end can talk to a peer.
        ///
        /// The host has no listener on a relay, so "the socket is up" only means "the relay accepted
        /// me" - a lone host would otherwise believe a peer exists and time its handshake out against
        /// nobody (the session layer drops a peer that stays quiet for five seconds). For the host the
        /// socket is therefore only connected once something has actually arrived from the relay,
        /// which cannot happen before the partner is paired and talking. The client keeps the plain
        /// meaning: it dials, then speaks first.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                // Socket.Connected is only "the state at the last I/O" and goes stale (it reads
                // false on a perfectly healthy idle socket). The link tracks the dial itself:
                // running is true only between a successful dial and the first failure.
                lock (gate) { return running && (hostRole ? peerHeard : true); }
            }
        }

        /// <summary>True once a peer's bytes have arrived (relay pairing evidence).</summary>
        public bool PeerHeard
        {
            get { lock (gate) { return peerHeard; } }
        }

        /// <summary>The relay has no UDP path; the session still calls this after authentication.</summary>
        public void ConfirmUnreliablePeer()
        {
        }

        /// <summary>Host role: there is nothing to listen on, both ends dial the relay.</summary>
        public void StartHost(int tcpPort, int udpPort)
        {
            lock (gate) { hostRole = true; peerHeard = false; }
            Dial();
        }

        /// <summary>Client role: the address given here is ignored, the relay's address is used.</summary>
        public void Connect(string host, int tcpPort, int udpPort)
        {
            lock (gate) { hostRole = false; peerHeard = false; }
            Dial();
        }

        private void Dial()
        {
            lock (gate)
            {
                if (running && socket != null)
                {
                    throw new InvalidOperationException("link already started");
                }
            }
            Open();
        }

        private void Open()
        {
            Socket fresh = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                fresh.NoDelay = true;
                fresh.Connect(new IPEndPoint(ResolveRelay(), relayPort));
                byte[] code = Encoding.ASCII.GetBytes(roomCode);
                int sent = 0;
                while (sent < code.Length)
                {
                    int n = fresh.Send(code, sent, code.Length - sent, SocketFlags.None);
                    if (n <= 0) throw new SocketException(10054);
                    sent += n;
                }
            }
            catch
            {
                CloseQuiet(fresh);
                throw;
            }
            lock (gate)
            {
                socket = fresh;
                running = true;
                overflowed = false;
                peerHeard = false;
                reliableOut.Clear();
                unreliableOut.Clear();
                incoming.Clear();
                queuedBytes = 0;
                queuedFrames = 0;
            }
            Diagnose("dialed relay " + relayHost + ":" + relayPort + " room=" + roomCode);
            StartThread(ReceiveLoop, "RelayRecv", out receiveThread);
            StartThread(SendLoop, "RelaySend", out sendThread);
        }

        private IPAddress ResolveRelay()
        {
            IPAddress address;
            if (IPAddress.TryParse(relayHost, out address))
            {
                return address;
            }
            IPAddress[] found = Dns.GetHostAddresses(relayHost);
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i].AddressFamily == AddressFamily.InterNetwork) return found[i];
            }
            throw new SocketException(11001);
        }

        public void Poll(int maxMessages)
        {
            pollCount++;
            if (diagnose && pollCount % 120 == 0)
            {
                // A gap between heartbeats means the game's main thread stopped calling Poll (a
                // scene load does exactly that); a steady beat next to a dead socket means the
                // transport died on its own. Either way this is what separates the two cases when
                // a relay session drops.
                int queued;
                int sent;
                lock (gate) { queued = queuedFrames; sent = framesReceived; }
                Diagnose("relay heartbeat polls=" + pollCount + " received=" + sent + " queued=" + queued);
            }
            List<InboundFrame> deliver = null;
            lock (gate)
            {
                while (incoming.Count > 0 && (maxMessages <= 0 || deliver == null || deliver.Count < maxMessages))
                {
                    InboundFrame frame = incoming.Dequeue();
                    queuedBytes -= frame.Payload.Length;
                    queuedFrames--;
                    if (deliver == null) deliver = new List<InboundFrame>();
                    deliver.Add(frame);
                }
            }
            if (deliver == null) return;
            for (int i = 0; i < deliver.Count; i++)
            {
                Action<byte[], bool> handler = OnFrame;
                if (handler == null) return;
                try
                {
                    handler(deliver[i].Payload, deliver[i].Reliable);
                }
                catch (Exception ex)
                {
                    Diagnose("frame handler threw: " + ex.Message);
                }
            }
        }

        public void SendReliable(byte[] payload)
        {
            Enqueue(payload, true);
        }

        public void SendUnreliable(byte[] payload)
        {
            Enqueue(payload, false);
        }

        private void Enqueue(byte[] payload, bool reliable)
        {
            if (payload == null || payload.Length == 0) return;
            if (payload.Length > MaxFrameBytes) throw new ArgumentException("relay frame too large: " + payload.Length);
            lock (gate)
            {
                if (!running || socket == null) return;
                if (queuedFrames >= maxQueuedFrames || queuedBytes + payload.Length > maxQueuedBytes)
                {
                    overflowed = true;
                    Monitor.PulseAll(gate);
                }
                else
                {
                    if (reliable) reliableOut.Enqueue(payload);
                    else unreliableOut.Enqueue(payload);
                    queuedFrames++;
                    queuedBytes += payload.Length;
                    Monitor.PulseAll(gate);
                }
            }
        }

        /// <summary>
        /// Drop the current pairing. The relay pairs by room code, so a re-dial puts this end back in
        /// the room - that is how the host releases a rejected peer and keeps a usable slot.
        /// </summary>
        public void DisconnectPeer()
        {
            Socket old;
            lock (gate)
            {
                old = socket;
                socket = null;
                running = false;
                reliableOut.Clear();
                unreliableOut.Clear();
                incoming.Clear();
                queuedBytes = 0;
                queuedFrames = 0;
                Monitor.PulseAll(gate);
            }
            CloseQuiet(old);
            ScheduleRedial();
        }

        public void Disconnect()
        {
            Socket old;
            lock (gate)
            {
                old = socket;
                socket = null;
                running = false;
                overflowed = false;
                reliableOut.Clear();
                unreliableOut.Clear();
                incoming.Clear();
                queuedBytes = 0;
                queuedFrames = 0;
                Monitor.PulseAll(gate);
            }
            CloseQuiet(old);
        }

        private void ScheduleRedial()
        {
            lock (gate)
            {
                if (redialThread != null) return;
                StartThread(delegate
                {
                    try { Thread.Sleep(RedialDelayMs); }
                    catch { }
                    lock (gate) { redialThread = null; if (running) return; }
                    try { Open(); }
                    catch (Exception ex) { Diagnose("redial failed: " + ex.Message); RaiseDisconnected(); }
                }, "RelayRedial", out redialThread);
            }
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[16 * 1024];
            List<byte[]> pending = new List<byte[]>();
            byte[] stash = new byte[0];
            while (true)
            {
                Socket current;
                lock (gate) { current = socket; if (!running || current == null) return; }
                int read;
                try
                {
                    read = current.Receive(buffer);
                }
                catch (Exception ex)
                {
                    if (IsCurrent(current))
                    {
                        Diagnose("relay receive ended: " + ex.Message);
                        DropFromBackground();
                    }
                    return;
                }
                if (read <= 0)
                {
                    if (IsCurrent(current))
                    {
                        Diagnose("relay closed the pair (read=0)");
                        DropFromBackground();
                    }
                    return;
                }
                byte[] chunk = new byte[stash.Length + read];
                Array.Copy(stash, chunk, stash.Length);
                Array.Copy(buffer, 0, chunk, stash.Length, read);
                pending.Clear();
                int offset = 0;
                while (true)
                {
                    if (chunk.Length - offset < 5) break;
                    uint length = (uint)(chunk[offset] | (chunk[offset + 1] << 8) | (chunk[offset + 2] << 16) | (chunk[offset + 3] << 24));
                    if (length == 0 || length > MaxFrameBytes + 1)
                    {
                        Diagnose("relay frame length out of range: " + length);
                        DropFromBackground();
                        return;
                    }
                    // length counts the channel tag plus the payload, so the bytes still needed after
                    // the tag are length - 1.
                    if (chunk.Length - offset - 5 < (int)length - 1) break;
                    byte tag = chunk[offset + 4];
                    byte[] payload = new byte[length - 1];
                    Array.Copy(chunk, offset + 5, payload, 0, payload.Length);
                    pending.Add(payload);
                    Deliver(payload, tag == ChannelReliable);
                    // wire layout is [length:4][tag:1][payload:length-1] -> 4 + length bytes total
                    offset += 4 + (int)length;
                }
                stash = new byte[chunk.Length - offset];
                Array.Copy(chunk, offset, stash, 0, stash.Length);
            }
        }

        private void Deliver(byte[] payload, bool reliable)
        {
            bool drop = false;
            lock (gate)
            {
                if (!running) return;
                peerHeard = true;
                if (queuedFrames >= maxQueuedFrames || queuedBytes + payload.Length > maxQueuedBytes)
                {
                    TrimUnreliableLocked();
                }
                if (queuedFrames >= maxQueuedFrames || queuedBytes + payload.Length > maxQueuedBytes)
                {
                    overflowed = true;
                    drop = true;
                }
                else
                {
                    InboundFrame frame = new InboundFrame();
                    frame.Payload = payload;
                    frame.Reliable = reliable;
                    incoming.Enqueue(frame);
                    queuedFrames++;
                    queuedBytes += payload.Length;
                    framesReceived++;
                }
            }
            if (drop)
            {
                Diagnose("relay incoming queue overflowed");
                DropFromBackground();
            }
        }

        /// <summary>
        /// Makes room by discarding already-queued snapshots. They are the newest-wins channel, so a
        /// dropped snapshot costs one stale frame; dropping the connection instead would cost the
        /// session. Reliable frames are never discarded - they carry the join/transition decisions.
        /// Must be called with the gate held.
        /// </summary>
        private void TrimUnreliableLocked()
        {
            List<InboundFrame> keep = new List<InboundFrame>(incoming.Count);
            long bytes = 0;
            bool trimmed = false;
            while (incoming.Count > 0)
            {
                InboundFrame frame = incoming.Dequeue();
                if (!frame.Reliable)
                {
                    trimmed = true;
                    continue;
                }
                keep.Add(frame);
                bytes += frame.Payload.Length;
            }
            for (int i = 0; i < keep.Count; i++)
            {
                incoming.Enqueue(keep[i]);
            }
            queuedFrames = keep.Count;
            queuedBytes = bytes;
            if (trimmed)
            {
                Diagnose("relay dropped queued snapshots to stay within the budget");
            }
        }

        private void SendLoop()
        {
            while (true)
            {
                byte[] payload = null;
                bool reliable = true;
                Socket current;
                lock (gate)
                {
                    for (int guard = 0; guard < 64 && payload == null; guard++)
                    {
                        if (!running || socket == null)
                        {
                            if (overflowed) return;
                            Monitor.Wait(gate, 200);
                            continue;
                        }
                        if (reliableOut.Count > 0)
                        {
                            payload = reliableOut.Dequeue();
                            reliable = true;
                        }
                        else if (unreliableOut.Count > 0)
                        {
                            payload = unreliableOut.Dequeue();
                            reliable = false;
                        }
                        else
                        {
                            Monitor.Wait(gate, 200);
                            continue;
                        }
                        queuedFrames--;
                        queuedBytes -= payload.Length;
                    }
                    current = socket;
                    if (!running || current == null)
                    {
                        if (payload == null) continue;
                        return;
                    }
                }
                if (payload == null) continue;
                uint length = (uint)(payload.Length + 1);
                byte[] wire = new byte[4 + 1 + payload.Length];
                wire[0] = (byte)(length & 0xFF);
                wire[1] = (byte)((length >> 8) & 0xFF);
                wire[2] = (byte)((length >> 16) & 0xFF);
                wire[3] = (byte)((length >> 24) & 0xFF);
                wire[4] = reliable ? ChannelReliable : ChannelUnreliable;
                Array.Copy(payload, 0, wire, 5, payload.Length);
                try
                {
                    int sent = 0;
                    while (sent < wire.Length)
                    {
                        int n = current.Send(wire, sent, wire.Length - sent, SocketFlags.None);
                        if (n <= 0) throw new SocketException(10054);
                        sent += n;
                    }
                }
                catch (Exception ex)
                {
                    if (IsCurrent(current))
                    {
                        Diagnose("relay send ended: " + ex.Message);
                        DropFromBackground();
                    }
                    return;
                }
            }
        }

        private bool IsCurrent(Socket candidate)
        {
            lock (gate) { return running && socket == candidate; }
        }

        private void DropFromBackground()
        {
            Socket old;
            lock (gate)
            {
                if (!running) return;
                old = socket;
                socket = null;
                running = false;
                reliableOut.Clear();
                unreliableOut.Clear();
                incoming.Clear();
                queuedBytes = 0;
                queuedFrames = 0;
                Monitor.PulseAll(gate);
            }
            CloseQuiet(old);
            Action handler = OnDisconnected;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Diagnose("disconnect handler threw: " + ex.Message); }
            }
        }

        private void RaiseDisconnected()
        {
            Action handler = OnDisconnected;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Diagnose("disconnect handler threw: " + ex.Message); }
            }
        }

        private static void StartThread(ThreadStart action, string name, out Thread thread)
        {
            thread = new Thread(action);
            thread.IsBackground = true;
            thread.Name = "RelayLink." + name;
            thread.Start();
        }

        private static void CloseQuiet(Socket target)
        {
            if (target == null) return;
            try { target.Close(); }
            catch { }
        }

        private void Diagnose(string message)
        {
            Action<string> handler = OnDiagnostic;
            if (handler == null) return;
            try { handler(message); }
            catch { }
        }
    }
}
