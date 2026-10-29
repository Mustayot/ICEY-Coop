using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IceyCoop.Protocol;

namespace IceyCoop.Transport
{
    /// <summary>Persistent host listener with per-peer decoder and socket ownership.
    /// Old IO failures cannot close a replacement peer. Poll owns user callbacks.</summary>
    public sealed class TcpUdpLink : INetworkLink
    {
        private sealed class Peer { public Socket Tcp; public TcpDecoder Decoder = new TcpDecoder(); }
        private sealed class Frame { public byte[] Bytes; public bool Reliable; public Peer Owner; public EndPoint Remote; }
        private sealed class Run
        {
            public bool Running = true;
            public bool Host;
            public bool TcpOnly;
            public bool Connecting;
            public bool Cancelled;
            public Socket Listener;
            public Socket Udp;
            public Socket ConnectingSocket;
            public ManualResetEvent ConnectCancelled;
            public Peer Peer;
            public EndPoint UdpRemote;
            public bool UdpBound;
            public bool DisconnectPending;
            public bool Overflowed;
            public int OversizedDrops;
            public long QueuedBytes;
            public readonly Queue<Frame> Incoming = new Queue<Frame>();
            public readonly Queue<Frame> Reliable = new Queue<Frame>();
            public readonly Queue<Frame> Unreliable = new Queue<Frame>();
            public readonly Queue<string> Diagnostics = new Queue<string>();
        }
        public const int MaxUdpDatagram = 1200;
        public const int DefaultMaxQueuedFrames = 1024;
        public const int DefaultMaxQueuedBytes = 64 * 1024;
        private readonly object gate = new object();
        private readonly int maxQueuedFrames;
        private readonly long maxQueuedBytes;
        private readonly bool tcpOnly;
        private Run current;
        private string lastConnectError;
        public TcpUdpLink() : this(DefaultMaxQueuedFrames, DefaultMaxQueuedBytes, false) { }
        public TcpUdpLink(bool tcpOnly) : this(DefaultMaxQueuedFrames, DefaultMaxQueuedBytes, tcpOnly) { }
        public TcpUdpLink(int maxQueuedFrames, int maxQueuedBytes)
            : this(maxQueuedFrames, maxQueuedBytes, false)
        {
        }
        private TcpUdpLink(int maxQueuedFrames, int maxQueuedBytes, bool tcpOnly)
        {
            if (maxQueuedFrames <= 0) throw new ArgumentOutOfRangeException("maxQueuedFrames");
            if (maxQueuedBytes <= 0) throw new ArgumentOutOfRangeException("maxQueuedBytes");
            this.maxQueuedFrames = maxQueuedFrames; this.maxQueuedBytes = maxQueuedBytes; this.tcpOnly = tcpOnly;
        }
        public bool IsConnected { get { lock (gate) { return current != null && current.Running && current.Peer != null; } } }
        public bool IsConnecting { get { lock (gate) { return current != null && current.Running && current.Connecting; } } }
        public string LastConnectError { get { lock (gate) { return lastConnectError; } } }
        public bool Overflowed { get { lock (gate) { return current != null && current.Overflowed; } } }
        public int OversizedUdpDrops { get { lock (gate) { return current == null ? 0 : current.OversizedDrops; } } }
        public event Action<byte[], bool> OnFrame;
        public event Action OnDisconnected;
        public event Action<string> OnDiagnostic;

        public void StartHost(int tcpPort, int udpPort)
        {
            EnsureStopped();
            Run run = new Run(); run.Host = true; run.TcpOnly = tcpOnly;
            try
            {
                run.Listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                run.Listener.Bind(new IPEndPoint(IPAddress.Any, tcpPort)); run.Listener.Listen(8);
                if (!run.TcpOnly)
                {
                    run.Udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    run.Udp.Bind(new IPEndPoint(IPAddress.Any, udpPort));
                }
            }
            catch { CloseSocket(run.Listener); CloseSocket(run.Udp); throw; }
            lock (gate) { current = run; }
            try
            {
                Start(delegate { AcceptLoop(run); }, "Accept");
                if (run.Udp != null) Start(delegate { UdpReceiveLoop(run); }, "UdpRecv");
                Start(delegate { SendLoop(run); }, "Send");
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
        public void Connect(string host, int tcpPort, int udpPort)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (tcpOnly)
            {
                ConnectTcpOnly(host, tcpPort);
                return;
            }
            EnsureStopped(); Run run = new Run(); Peer peer = new Peer();
            try
            {
                IPAddress address = ResolveHost(host);
                peer.Tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                peer.Tcp.Connect(new IPEndPoint(address, tcpPort)); peer.Tcp.NoDelay = true;
                run.Udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                run.Udp.Bind(new IPEndPoint(IPAddress.Any, 0));
                run.UdpRemote = new IPEndPoint(address, udpPort);
            }
            catch { CloseSocket(peer.Tcp); CloseSocket(run.Udp); throw; }
            run.Peer = peer; lock (gate) { current = run; lastConnectError = null; }
            try
            {
                Start(delegate { TcpReceiveLoop(run, peer); }, "TcpRecv");
                Start(delegate { UdpReceiveLoop(run); }, "UdpRecv");
                Start(delegate { SendLoop(run); }, "Send");
            }
            catch
            {
                Disconnect();
                throw;
            }
        }

        private void ConnectTcpOnly(string host, int tcpPort)
        {
            EnsureStopped();
            Run run = new Run();
            run.TcpOnly = true;
            run.Host = false;
            run.Connecting = true;
            run.ConnectCancelled = new ManualResetEvent(false);
            lock (gate)
            {
                current = run;
                lastConnectError = null;
            }
            try
            {
                Start(delegate { ConnectTcpOnlyWorker(run, host, tcpPort); }, "Connect");
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (current == run)
                    {
                        run.Connecting = false;
                        run.Running = false;
                        lastConnectError = ex.Message;
                        if (run.ConnectCancelled != null)
                        {
                            run.ConnectCancelled.Close();
                            run.ConnectCancelled = null;
                        }
                    }
                }
                Diagnose(run, "tcp-only connect worker failed: " + ex.Message);
            }
        }

        private void ConnectTcpOnlyWorker(Run run, string host, int tcpPort)
        {
            DateTime started = DateTime.UtcNow;
            Socket socket = null;
            ManualResetEvent cancelled = run.ConnectCancelled;
            try
            {
                IPAddress address = ResolveHostBounded(host, started, cancelled);
                if (address == null) return;
                if (!IsCurrentConnect(run)) return;

                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                lock (gate)
                {
                    if (current != run || !run.Running || !run.Connecting || run.Cancelled)
                    {
                        CloseSocket(socket);
                        return;
                    }
                    run.ConnectingSocket = socket;
                }

                IAsyncResult result = socket.BeginConnect(new IPEndPoint(address, tcpPort), null, null);
                WaitHandle connectWait = result.AsyncWaitHandle;
                try
                {
                    int remaining = RemainingConnectMs(started);
                    WaitHandle[] waits = new WaitHandle[] { connectWait, cancelled };
                    int signaled = remaining <= 0 ? WaitHandle.WaitTimeout : WaitHandle.WaitAny(waits, remaining, false);
                    if (signaled == 1) return;
                    if (signaled == WaitHandle.WaitTimeout)
                        throw new TimeoutException("TCP connect timed out after 8 seconds");
                    socket.EndConnect(result);
                }
                finally { connectWait.Close(); }

                Peer peer = new Peer { Tcp = socket };
                lock (gate)
                {
                    if (current != run || !run.Running || !run.Connecting || run.Cancelled)
                    {
                        run.ConnectingSocket = null;
                        CloseSocket(socket);
                        return;
                    }
                    run.Peer = peer;
                    run.ConnectingSocket = null;
                    run.Connecting = false;
                    lastConnectError = null;
                }

                try
                {
                    Start(delegate { TcpReceiveLoop(run, peer); }, "TcpRecv");
                    Start(delegate { SendLoop(run); }, "Send");
                    Diagnose(run, "tcp-only connected to " + address + ":" + tcpPort);
                }
                catch
                {
                    DropPeer(run, peer);
                    throw;
                }
            }
            catch (Exception ex)
            {
                CloseSocket(socket);
                bool report = false;
                lock (gate)
                {
                    if (current == run && run.Running && !run.Cancelled)
                    {
                        run.ConnectingSocket = null;
                        run.Connecting = false;
                        run.Running = false;
                        lastConnectError = ex.Message;
                        report = true;
                    }
                }
                if (report) Diagnose(run, "tcp-only connect failed: " + ex.Message);
            }
            finally
            {
                lock (gate)
                {
                    if (run.ConnectCancelled == cancelled) run.ConnectCancelled = null;
                }
                if (cancelled != null) cancelled.Close();
            }
        }

        private bool IsCurrentConnect(Run run)
        {
            lock (gate) { return current == run && run.Running && run.Connecting && !run.Cancelled; }
        }

        private static IPAddress ResolveHostBounded(string host, DateTime started, ManualResetEvent cancelled)
        {
            if (cancelled.WaitOne(0, false)) return null;
            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
            {
                if (parsed.AddressFamily == AddressFamily.InterNetwork) return parsed;
                throw new SocketException(10047);
            }

            IAsyncResult result = Dns.BeginGetHostAddresses(host, null, null);
            WaitHandle dnsWait = result.AsyncWaitHandle;
            try
            {
                int remaining = RemainingConnectMs(started);
                WaitHandle[] waits = new WaitHandle[] { dnsWait, cancelled };
                int signaled = remaining <= 0 ? WaitHandle.WaitTimeout : WaitHandle.WaitAny(waits, remaining, false);
                if (signaled == 1) return null;
                if (signaled == WaitHandle.WaitTimeout)
                    throw new TimeoutException("DNS lookup timed out after 8 seconds");
                IPAddress[] addresses = Dns.EndGetHostAddresses(result);
                for (int i = 0; i < addresses.Length; i++)
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork) return addresses[i];
                throw new SocketException(11001);
            }
            finally { dnsWait.Close(); }
        }

        private static int RemainingConnectMs(DateTime started)
        {
            long elapsed = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            return elapsed >= 8000 ? 0 : 8000 - (int)elapsed;
        }
        public void Poll(int maxMessages)
        {
            if (maxMessages < 0) throw new ArgumentOutOfRangeException("maxMessages");
            Run run; bool disconnected;
            lock (gate)
            {
                run = current; if (run == null) return;
                disconnected = run.DisconnectPending; run.DisconnectPending = false;
            }
            if (disconnected) { Action handler = OnDisconnected; if (handler != null) handler(); }
            if (run.TcpOnly) PollDiagnostics(run);
            for (int i = 0; i < maxMessages; i++)
            {
                Frame frame = null;
                bool peerChanged;
                lock (gate)
                {
                    if (current != run) return;
                    peerChanged = run.DisconnectPending;
                    if (peerChanged) run.DisconnectPending = false;
                    else
                    {
                        if (run.Incoming.Count == 0) return;
                        frame = run.Incoming.Dequeue(); run.QueuedBytes -= frame.Bytes.Length;
                        if (frame.Owner != run.Peer) continue;
                        if (!frame.Reliable && run.Host && !run.TcpOnly && !run.UdpBound) run.UdpRemote = frame.Remote;
                    }
                }
                if (peerChanged)
                {
                    Action disconnectedHandler = OnDisconnected;
                    if (disconnectedHandler != null) disconnectedHandler();
                    continue;
                }
                Action<byte[], bool> handler = OnFrame;
                // Keep peer ownership stable through the callback. The monitor is
                // re-entrant, so session replies/disconnects can use this link.
                lock (gate)
                {
                    if (current != run || frame.Owner != run.Peer || run.DisconnectPending) continue;
                    if (handler != null) handler(frame.Bytes, frame.Reliable);
                }
            }
        }
        public void SendReliable(byte[] payload) { QueueOutgoing(payload, true); }
        public void SendUnreliable(byte[] payload) { QueueOutgoing(payload, false); }
        public void ConfirmUnreliablePeer()
        {
            if (tcpOnly) return;
            lock (gate) { if (current != null && current.Peer != null && current.UdpRemote != null) current.UdpBound = true; }
        }
        private void QueueOutgoing(byte[] payload, bool reliable)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            lock (gate)
            {
                Run run = current; if (run == null || !run.Running || run.Peer == null) return;
                Frame frame = new Frame { Bytes = payload, Reliable = reliable, Owner = run.Peer };
                (reliable ? run.Reliable : run.Unreliable).Enqueue(frame); Monitor.PulseAll(gate);
            }
        }
        /// <summary>Drop the peer, retaining the host listener and any configured UDP socket.</summary>
        public void DisconnectPeer()
        {
            Run run; Peer peer;
            lock (gate) { run = current; peer = run == null ? null : run.Peer; }
            if (peer != null) DropPeer(run, peer);
        }
        public void Disconnect()
        {
            Run run;
            Socket connectingSocket;
            lock (gate)
            {
                run = current; if (run == null) return;
                run.Cancelled = true;
                run.Connecting = false;
                if (run.ConnectCancelled != null) run.ConnectCancelled.Set();
                connectingSocket = run.ConnectingSocket;
                run.ConnectingSocket = null;
                if (!run.Running && run.Peer == null && connectingSocket == null) return;
                run.Running = false; run.DisconnectPending = true; Monitor.PulseAll(gate);
            }
            CloseSocket(run.Listener); CloseSocket(run.Udp); CloseSocket(connectingSocket);
            Peer peer; lock (gate) { peer = run.Peer; }
            if (peer != null) DropPeer(run, peer);
        }
        private void EnsureStopped()
        {
            lock (gate) { if (current != null && (current.Running || current.Connecting)) throw new InvalidOperationException("link already started"); }
        }
        private void AcceptLoop(Run run)
        {
            try
            {
                while (IsRunning(run))
                {
                    Socket socket = run.Listener.Accept(); Peer peer = new Peer { Tcp = socket }; bool accepted;
                    lock (gate)
                    {
                        accepted = run.Running && run.Peer == null;
                        if (accepted) { run.Peer = peer; run.UdpRemote = null; run.UdpBound = false; }
                    }
                    if (!accepted) { CloseSocket(socket); continue; }
                    socket.NoDelay = true; Diagnose("accepted tcp peer");
                    Start(delegate { TcpReceiveLoop(run, peer); }, "TcpRecv");
                }
            }
            catch (Exception ex) { if (IsRunning(run)) Diagnose("accept loop ended: " + ex); }
        }
        private void TcpReceiveLoop(Run run, Peer peer)
        {
            try
            {
                byte[] buffer = new byte[65536];
                while (IsPeer(run, peer))
                {
                    int n = peer.Tcp.Receive(buffer);
                    if (n == 0) { Diagnose("tcp peer closed stream"); break; }
                    byte[] chunk = new byte[n]; Array.Copy(buffer, chunk, n);
                    IList<byte[]> frames = peer.Decoder.Push(chunk);
                    foreach (byte[] bytes in frames)
                    {
                        if (!run.TcpOnly)
                        {
                            EnqueueIncoming(run, new Frame { Bytes = bytes, Reliable = true, Owner = peer });
                            continue;
                        }
                        byte[] payload;
                        bool reliable;
                        string error;
                        if (!TryDecodeTunnelEnvelope(bytes, out payload, out reliable, out error))
                        {
                            Diagnose(run, "tcp-only envelope rejected: " + error);
                            DropPeer(run, peer);
                            return;
                        }
                        EnqueueIncoming(run, new Frame { Bytes = payload, Reliable = reliable, Owner = peer });
                    }
                }
            }
            catch (Exception ex) { if (IsPeer(run, peer)) Diagnose("tcp recv peer ended: " + ex); }
            finally { DropPeer(run, peer); }
        }
        private void UdpReceiveLoop(Run run)
        {
            try
            {
                byte[] buffer = new byte[2048];
                while (IsRunning(run))
                {
                    EndPoint remote = new IPEndPoint(IPAddress.Any, 0); int n = run.Udp.ReceiveFrom(buffer, ref remote); Peer peer;
                    lock (gate)
                    {
                        if (n > MaxUdpDatagram) { run.OversizedDrops++; continue; }
                        peer = run.Peer; if (peer == null) continue;
                        IPEndPoint tcpRemote = peer.Tcp.RemoteEndPoint as IPEndPoint;
                        if (tcpRemote == null || !tcpRemote.Address.Equals(((IPEndPoint)remote).Address)) continue;
                        if ((!run.Host || run.UdpBound) && run.UdpRemote != null && !run.UdpRemote.Equals(remote)) continue;
                    }
                    byte[] bytes = new byte[n]; Array.Copy(buffer, bytes, n);
                    EnqueueIncoming(run, new Frame { Bytes = bytes, Owner = peer, Remote = remote });
                }
            }
            catch (Exception ex) { if (IsRunning(run)) Diagnose("udp recv loop ended: " + ex); }
        }
        private void EnqueueIncoming(Run run, Frame frame)
        {
            bool overflow = false;
            lock (gate)
            {
                if (!run.Running || frame.Owner != run.Peer) return;
                if (run.Incoming.Count >= maxQueuedFrames || run.QueuedBytes + frame.Bytes.Length > maxQueuedBytes)
                { run.Overflowed = true; overflow = true; }
                else { run.Incoming.Enqueue(frame); run.QueuedBytes += frame.Bytes.Length; }
            }
            if (overflow) DropPeer(run, frame.Owner);
        }
        private void SendLoop(Run run)
        {
            while (IsRunning(run))
            {
                Frame frame; EndPoint remote;
                lock (gate)
                {
                    if (!run.Running) return;
                    if (run.Reliable.Count > 0) frame = run.Reliable.Dequeue();
                    else if (run.Unreliable.Count > 0) frame = run.Unreliable.Dequeue();
                    else { Monitor.Wait(gate, 200); continue; }
                    if (frame.Owner != run.Peer) continue;
                    remote = run.UdpRemote;
                }
                try
                {
                    if (run.TcpOnly)
                    {
                        byte[] envelope = EncodeTunnelEnvelope(frame.Bytes, frame.Reliable);
                        byte[] wire = EncodeTcpFrame(envelope); int sent = 0;
                        while (sent < wire.Length)
                        {
                            int n = frame.Owner.Tcp.Send(wire, sent, wire.Length - sent, SocketFlags.None);
                            if (n <= 0) throw new SocketException(10054); sent += n;
                        }
                    }
                    else if (frame.Reliable)
                    {
                        byte[] wire = EncodeTcpFrame(frame.Bytes); int sent = 0;
                        while (sent < wire.Length)
                        {
                            int n = frame.Owner.Tcp.Send(wire, sent, wire.Length - sent, SocketFlags.None);
                            if (n <= 0) throw new SocketException(10054); sent += n;
                        }
                    }
                    else if (remote != null) run.Udp.SendTo(frame.Bytes, remote);
                }
                catch (Exception ex)
                {
                    if (IsPeer(run, frame.Owner)) Diagnose("send peer ended: " + ex);
                    DropPeer(run, frame.Owner);
                }
            }
        }
        private void DropPeer(Run run, Peer peer)
        {
            bool closeUdp = false;
            lock (gate)
            {
                if (run.Peer != peer) return;
                run.Peer = null; run.UdpRemote = null;
                run.Incoming.Clear(); run.Reliable.Clear(); run.Unreliable.Clear(); run.QueuedBytes = 0;
                run.DisconnectPending = true;
                if (!run.Host) { run.Running = false; closeUdp = true; }
                Monitor.PulseAll(gate);
            }
            CloseSocket(peer.Tcp); if (closeUdp) CloseSocket(run.Udp);
        }
        private bool IsRunning(Run run) { lock (gate) { return run.Running; } }
        private bool IsPeer(Run run, Peer peer) { lock (gate) { return run.Running && run.Peer == peer; } }
        private static void Start(ThreadStart action, string name)
        {
            Thread thread = new Thread(action); thread.IsBackground = true; thread.Name = "TcpUdpLink." + name; thread.Start();
        }
        private void Diagnose(string message)
        {
            Run run;
            lock (gate) { run = current; }
            Diagnose(run, message);
        }
        private void Diagnose(Run run, string message)
        {
            if (run != null && run.TcpOnly)
            {
                lock (gate)
                {
                    if (run.Diagnostics.Count < 256) run.Diagnostics.Enqueue(message);
                }
                return;
            }
            Action<string> handler = OnDiagnostic; if (handler != null) { try { handler(message); } catch { } }
        }
        private void PollDiagnostics(Run run)
        {
            while (true)
            {
                string message;
                lock (gate)
                {
                    if (current != run || run.Diagnostics.Count == 0) return;
                    message = run.Diagnostics.Dequeue();
                }
                Action<string> handler = OnDiagnostic;
                if (handler != null) { try { handler(message); } catch { } }
            }
        }
        private static byte[] EncodeTunnelEnvelope(byte[] payload, bool reliable)
        {
            payload = payload ?? new byte[0];
            byte[] envelope = new byte[5 + payload.Length];
            envelope[0] = 0x49; envelope[1] = 0x43; envelope[2] = 0x54; envelope[3] = 0x01;
            envelope[4] = reliable ? (byte)0 : (byte)1;
            Array.Copy(payload, 0, envelope, 5, payload.Length);
            return envelope;
        }
        private static bool TryDecodeTunnelEnvelope(byte[] envelope, out byte[] payload,
            out bool reliable, out string error)
        {
            payload = null;
            reliable = false;
            error = null;
            if (envelope == null || envelope.Length < 5)
            {
                error = "missing 5-byte header";
                return false;
            }
            if (envelope[0] != 0x49 || envelope[1] != 0x43 || envelope[2] != 0x54 || envelope[3] != 0x01)
            {
                error = "magic/version mismatch (expected 49 43 54 01)";
                return false;
            }
            if (envelope[4] > 1)
            {
                error = "unsupported reliable flag " + envelope[4];
                return false;
            }
            reliable = envelope[4] == 0;
            payload = new byte[envelope.Length - 5];
            Array.Copy(envelope, 5, payload, 0, payload.Length);
            return true;
        }
        private static byte[] EncodeTcpFrame(byte[] payload)
        {
            byte[] wire = new byte[4 + payload.Length];
            uint length = (uint)payload.Length;
            for (int i = 0; i < 4; i++) wire[i] = (byte)(length >> (i * 8));
            Array.Copy(payload, 0, wire, 4, payload.Length);
            return wire;
        }
        private static IPAddress ResolveHost(string host)
        {
            IPAddress parsed; if (IPAddress.TryParse(host, out parsed)) return parsed;
            foreach (IPAddress address in Dns.GetHostAddresses(host)) if (address.AddressFamily == AddressFamily.InterNetwork) return address;
            throw new SocketException(11001);
        }
        private static void CloseSocket(Socket socket) { if (socket != null) { try { socket.Close(); } catch (Exception) { } } }
    }
}
