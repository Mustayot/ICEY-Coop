using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    public sealed class TcpTunnelSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "TcpTunnel";

            s.Tests.Add(new TestCase
            {
                Name = "tcp-only carries reliable and unreliable frames with original flags",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink(true);
                    TcpUdpLink client = new TcpUdpLink(true);
                    Collector receivedByHost = new Collector();
                    Collector receivedByClient = new Collector();
                    receivedByHost.Attach(host);
                    receivedByClient.Attach(client);
                    UdpClient occupiedUdp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    int hostUdpPort = ((IPEndPoint)occupiedUdp.Client.LocalEndPoint).Port;
                    int tcpPort = FreeTcpPort();
                    try
                    {
                        host.StartHost(tcpPort, hostUdpPort);
                        client.Connect("127.0.0.1", tcpPort, 0);
                        Assert.True(WaitConnected(host, client, 3000), "tcp-only links connect without a UDP target");

                        byte[] reliable = new byte[] { 1, 2, 3, 4 };
                        byte[] unreliable = new byte[] { 9, 8, 7 };
                        client.SendReliable(reliable);
                        client.SendUnreliable(unreliable);
                        Assert.True(Drain(host, client, receivedByHost, receivedByClient, 2, 3000), "host receives both frame classes");
                        AssertFrame(receivedByHost, 0, reliable, true, "reliable frame");
                        AssertFrame(receivedByHost, 1, unreliable, false, "unreliable frame");
                        Assert.True(receivedByHost.AllCallbacksOn(Thread.CurrentThread.ManagedThreadId),
                            "tcp-only callbacks run on the Poll caller thread");

                        host.SendUnreliable(new byte[] { 5, 6 });
                        Assert.True(DrainClient(host, client, receivedByClient, 1, 3000), "client receives unreliable frame");
                        AssertFrame(receivedByClient, 0, new byte[] { 5, 6 }, false, "reverse unreliable frame");
                    }
                    finally
                    {
                        client.Disconnect();
                        host.Disconnect();
                        occupiedUdp.Close();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid tcp-only envelope diagnoses and drops only the current peer",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink(true);
                    Collector collector = new Collector();
                    collector.Attach(host);
                    int tcpPort = FreeTcpPort();
                    host.StartHost(tcpPort, 0);
                    Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        raw.Connect(new IPEndPoint(IPAddress.Loopback, tcpPort));
                        Assert.True(WaitAccepted(host, 2000), "raw peer accepted");
                        byte[] malformed = FrameOnWire(new byte[] { 0, 0, 0, 0, 0, 0 });
                        SendAll(raw, malformed);

                        Assert.True(WaitDisconnected(host, collector, 3000), "malformed envelope drops peer");
                        Assert.True(collector.HasDiagnosticContaining("tcp-only"), "diagnostic names tcp-only envelope");
                        Assert.True(!host.IsConnected, "host listener remains with no peer");

                        raw.Close();
                        raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        raw.Connect(new IPEndPoint(IPAddress.Loopback, tcpPort));
                        Assert.True(WaitAccepted(host, 2000), "host accepts replacement peer");
                        SendAll(raw, FrameOnWire(new byte[] { 0x49, 0x43, 0x54, 0x01, 2 }));
                        Assert.True(WaitDisconnectedCount(host, collector, 2, 3000), "unsupported flag drops replacement peer");
                        Assert.True(collector.HasDiagnosticContaining("flag"), "unsupported flag has a specific diagnostic");
                        Assert.True(collector.AllCallbacksOn(Thread.CurrentThread.ManagedThreadId),
                            "diagnostic and disconnect callbacks run on Poll caller thread");
                    }
                    finally
                    {
                        raw.Close();
                        host.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "tcp-only connect is asynchronous and reports refused sockets",
                Run = delegate
                {
                    TcpUdpLink client = new TcpUdpLink(true);
                    Collector collector = new Collector();
                    collector.Attach(client);
                    int closedPort = FreeTcpPort();
                    Stopwatch watch = Stopwatch.StartNew();
                    try
                    {
                        client.Connect("127.0.0.1", closedPort, 0);
                        watch.Stop();
                        Assert.True(watch.ElapsedMilliseconds < 500, "Connect returns without waiting on socket completion");

                        int waited = 0;
                        while (client.IsConnecting && waited < 9000)
                        {
                            client.Poll(16);
                            Thread.Sleep(10);
                            waited += 10;
                        }
                        client.Poll(16);
                        Assert.True(!client.IsConnecting, "connect worker completes within bounded window");
                        Assert.True(!string.IsNullOrEmpty(client.LastConnectError), "connect error is available to caller");
                        Assert.True(!client.IsConnected, "failed connection does not install a peer");
                    }
                    finally
                    {
                        client.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "disconnect cancels pending tcp-only connect without late resurrection",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink(true);
                    TcpUdpLink client = new TcpUdpLink(true);
                    TcpUdpLink replacement = new TcpUdpLink(true);
                    Collector hc = new Collector();
                    Collector cc = new Collector();
                    Collector rc = new Collector();
                    hc.Attach(host);
                    cc.Attach(client);
                    rc.Attach(replacement);
                    int tcpPort = FreeTcpPort();
                    host.StartHost(tcpPort, 0);
                    try
                    {
                        client.Connect("127.0.0.1", tcpPort, 0);
                        client.Disconnect();
                        int waited = 0;
                        while (waited < 500)
                        {
                            client.Poll(16);
                            host.Poll(16);
                            Thread.Sleep(10);
                            waited += 10;
                        }
                        Assert.True(!client.IsConnecting, "cancel clears connecting state");
                        Assert.True(!client.IsConnected, "late connect completion cannot restore client peer");

                        Assert.True(WaitHostAvailable(host, 2000), "host listener survives canceled client");
                        replacement.Connect("127.0.0.1", tcpPort, 0);
                        Assert.True(WaitConnected(host, replacement, 3000), "replacement client can connect");
                    }
                    finally
                    {
                        client.Disconnect();
                        replacement.Disconnect();
                        host.Disconnect();
                    }
                }
            });

            return s;
        }

        private sealed class Collector
        {
            private readonly object gate = new object();
            private readonly List<byte[]> frames = new List<byte[]>();
            private readonly List<bool> reliability = new List<bool>();
            private readonly List<string> diagnostics = new List<string>();
            private readonly List<int> callbackThreads = new List<int>();
            public int Disconnects;

            public void Attach(TcpUdpLink link)
            {
                link.OnFrame += delegate(byte[] bytes, bool reliable)
                {
                    lock (gate)
                    {
                        frames.Add(bytes);
                        reliability.Add(reliable);
                        callbackThreads.Add(Thread.CurrentThread.ManagedThreadId);
                    }
                };
                link.OnDisconnected += delegate
                {
                    lock (gate) { callbackThreads.Add(Thread.CurrentThread.ManagedThreadId); }
                    Interlocked.Increment(ref Disconnects);
                };
                link.OnDiagnostic += delegate(string message)
                {
                    lock (gate)
                    {
                        diagnostics.Add(message);
                        callbackThreads.Add(Thread.CurrentThread.ManagedThreadId);
                    }
                };
            }

            public int Count { get { lock (gate) { return frames.Count; } } }

            public byte[] FrameAt(int index)
            {
                lock (gate) { return frames[index]; }
            }

            public bool ReliableAt(int index)
            {
                lock (gate) { return reliability[index]; }
            }

            public bool HasDiagnosticContaining(string text)
            {
                lock (gate)
                {
                    for (int i = 0; i < diagnostics.Count; i++)
                        if (diagnostics[i].IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    return false;
                }
            }

            public bool AllCallbacksOn(int threadId)
            {
                lock (gate)
                {
                    for (int i = 0; i < callbackThreads.Count; i++)
                        if (callbackThreads[i] != threadId) return false;
                    return callbackThreads.Count > 0;
                }
            }
        }

        private static void AssertFrame(Collector collector, int index, byte[] expected, bool reliable, string label)
        {
            Assert.Equal(expected.Length, collector.FrameAt(index).Length, label + " length");
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], collector.FrameAt(index)[i], label + " byte " + i);
            Assert.Equal(reliable, collector.ReliableAt(index), label + " reliability");
        }

        private static bool Drain(TcpUdpLink host, TcpUdpLink client, Collector hostCollector,
            Collector clientCollector, int wantHost, int timeoutMs)
        {
            int waited = 0;
            while (hostCollector.Count < wantHost && waited < timeoutMs)
            {
                host.Poll(32);
                client.Poll(32);
                Thread.Sleep(5);
                waited += 5;
            }
            host.Poll(32);
            client.Poll(32);
            return hostCollector.Count >= wantHost;
        }

        private static bool DrainClient(TcpUdpLink host, TcpUdpLink client, Collector clientCollector,
            int wantClient, int timeoutMs)
        {
            int waited = 0;
            while (clientCollector.Count < wantClient && waited < timeoutMs)
            {
                host.Poll(32);
                client.Poll(32);
                Thread.Sleep(5);
                waited += 5;
            }
            host.Poll(32);
            client.Poll(32);
            return clientCollector.Count >= wantClient;
        }

        private static bool WaitConnected(TcpUdpLink host, TcpUdpLink client, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                host.Poll(32);
                client.Poll(32);
                if (host.IsConnected && client.IsConnected) return true;
                Thread.Sleep(5);
                waited += 5;
            }
            return false;
        }

        private static bool WaitAccepted(TcpUdpLink host, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                host.Poll(32);
                if (host.IsConnected) return true;
                Thread.Sleep(5);
                waited += 5;
            }
            return false;
        }

        private static bool WaitDisconnected(TcpUdpLink host, Collector collector, int timeoutMs)
        {
            return WaitDisconnectedCount(host, collector, 1, timeoutMs);
        }

        private static bool WaitDisconnectedCount(TcpUdpLink host, Collector collector, int count, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                host.Poll(32);
                if (Volatile.Read(ref collector.Disconnects) >= count) return true;
                Thread.Sleep(5);
                waited += 5;
            }
            host.Poll(32);
            return Volatile.Read(ref collector.Disconnects) >= count;
        }

        private static bool WaitHostAvailable(TcpUdpLink host, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                host.Poll(32);
                if (!host.IsConnected) return true;
                Thread.Sleep(5);
                waited += 5;
            }
            return !host.IsConnected;
        }

        private static int FreeTcpPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static byte[] FrameOnWire(byte[] payload)
        {
            byte[] result = new byte[4 + payload.Length];
            uint length = (uint)payload.Length;
            for (int i = 0; i < 4; i++) result[i] = (byte)(length >> (i * 8));
            Array.Copy(payload, 0, result, 4, payload.Length);
            return result;
        }

        private static void SendAll(Socket socket, byte[] bytes)
        {
            int sent = 0;
            while (sent < bytes.Length)
            {
                int count = socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                if (count <= 0) throw new SocketException(10054);
                sent += count;
            }
        }
    }
}
