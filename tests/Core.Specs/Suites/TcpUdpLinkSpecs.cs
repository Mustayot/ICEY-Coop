using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IceyCoop.Protocol;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    public sealed class TcpUdpLinkSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "TcpUdpLink";

            s.Tests.Add(new TestCase
            {
                Name = "reliable handshake frames flow both ways",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    TcpUdpLink client = new TcpUdpLink();
                    Collector hc = new Collector();
                    Collector cc = new Collector();
                    hc.Attach(host);
                    cc.Attach(client);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "connected");

                        byte[] hello = MakeFrame(MessageKinds.Hello, 1, new byte[] { 1, 2, 3 });
                        byte[] welcome = MakeFrame(MessageKinds.Welcome, 2, new byte[] { 4, 5 });
                        client.SendReliable(hello);
                        host.SendReliable(welcome);

                        Assert.True(Drain(host, hc, 1, 3000), "host got hello");
                        Assert.True(Drain(client, cc, 1, 3000), "client got welcome");

                        byte[] h0;
                        lock (hc) { h0 = hc.Reliable[0]; }
                        FrameHeader hh = HeaderCodec.Read(h0);
                        Assert.Equal(MessageKinds.Hello, hh.MessageKind, "hello kind");
                        Assert.Equal((ulong)1, hh.MessageSeq, "hello seq");
                        byte[] hp = PayloadOf(h0);
                        Assert.Equal(3, hp.Length, "hello payload len");
                        Assert.Equal((byte)1, hp[0], "hello payload byte0");
                        Assert.Equal((byte)3, hp[2], "hello payload byte2");

                        byte[] c0;
                        lock (cc) { c0 = cc.Reliable[0]; }
                        Assert.Equal(MessageKinds.Welcome, HeaderCodec.Read(c0).MessageKind, "welcome kind");
                    }
                    finally
                    {
                        client.Disconnect();
                        host.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "half packet over tcp is reassembled",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    Collector hc = new Collector();
                    hc.Attach(host);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        raw.Connect(new IPEndPoint(IPAddress.Loopback, HostTcpPort));
                        byte[] frame = MakeFrame(MessageKinds.Ping, 5, PatternPayload(16));
                        byte[] wire = FrameOnWire(frame);
                        raw.Send(wire, 0, 2, SocketFlags.None);
                        Thread.Sleep(80);
                        raw.Send(wire, 2, wire.Length - 2, SocketFlags.None);

                        Assert.True(Drain(host, hc, 1, 3000), "half packet reassembled");
                        byte[] got;
                        lock (hc) { got = hc.Reliable[0]; }
                        Assert.Equal(MessageKinds.Ping, HeaderCodec.Read(got).MessageKind, "kind");
                        Assert.Equal(16, PayloadOf(got).Length, "payload intact");
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
                Name = "sticky packets split into two frames",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    Collector hc = new Collector();
                    hc.Attach(host);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        raw.Connect(new IPEndPoint(IPAddress.Loopback, HostTcpPort));
                        byte[] a = FrameOnWire(MakeFrame(MessageKinds.Ping, 1, new byte[] { 10 }));
                        byte[] b = FrameOnWire(MakeFrame(MessageKinds.Pong, 2, new byte[] { 20, 21 }));
                        raw.Send(Concat(a, b));

                        Assert.True(Drain(host, hc, 2, 3000), "two frames");
                        byte[] f0;
                        byte[] f1;
                        lock (hc) { f0 = hc.Reliable[0]; f1 = hc.Reliable[1]; }
                        Assert.Equal(MessageKinds.Ping, HeaderCodec.Read(f0).MessageKind, "first ping");
                        Assert.Equal(MessageKinds.Pong, HeaderCodec.Read(f1).MessageKind, "second pong");
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
                Name = "large 1MiB frame arrives intact",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink(1024, 2 * 1024 * 1024);
                    TcpUdpLink client = new TcpUdpLink();
                    Collector hc = new Collector();
                    hc.Attach(host);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "connected");

                        int payloadSize = ProtocolConstants.MaxReliableFrameLength - ProtocolConstants.HeaderLength;
                        byte[] frame = MakeFrame(MessageKinds.WorldEvent, 7, PatternPayload(payloadSize));
                        client.SendReliable(frame);

                        Assert.True(Drain(host, hc, 1, 8000), "large frame received");
                        byte[] got;
                        lock (hc) { got = hc.Reliable[0]; }
                        Assert.Equal(frame.Length, got.Length, "same length");
                        bool same = true;
                        for (int i = 0; i < frame.Length; i++)
                        {
                            if (frame[i] != got[i])
                            {
                                same = false;
                                break;
                            }
                        }
                        Assert.True(same, "1MiB byte-identical");
                    }
                    finally
                    {
                        client.Disconnect();
                        host.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "udp traffic does not disturb reliable path",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    TcpUdpLink client = new TcpUdpLink();
                    Collector hc = new Collector();
                    hc.Attach(host);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "connected");

                        client.SendUnreliable(new byte[100]);
                        client.SendUnreliable(new byte[200]);
                        byte[] reliable = MakeFrame(MessageKinds.InputBatch, 9, new byte[] { 7 });
                        client.SendReliable(reliable);

                        Assert.True(Drain(host, hc, 1, 3000), "reliable frame delivered");
                        byte[] got;
                        lock (hc) { got = hc.Reliable[0]; }
                        Assert.Equal(MessageKinds.InputBatch, HeaderCodec.Read(got).MessageKind, "kind");
                        Assert.Equal((byte)7, PayloadOf(got)[0], "payload intact");

                        // UDP may drop, but must never exceed what was sent on this path.
                        lock (hc)
                        {
                            Assert.True(hc.Unreliable.Count <= 2, "no duplicated udp bursts");
                        }
                    }
                    finally
                    {
                        client.Disconnect();
                        host.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "oversized udp datagram dropped and counted",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    try
                    {
                        byte[] big = new byte[1300];
                        big[0] = 0xAB;
                        raw.SendTo(big, new IPEndPoint(IPAddress.Loopback, HostUdpPort));

                        int waited = 0;
                        while (host.OversizedUdpDrops == 0 && waited < 2000)
                        {
                            Thread.Sleep(10);
                            host.Poll(4);
                            waited += 10;
                        }
                        Assert.True(host.OversizedUdpDrops >= 1, "oversized datagram counted");
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
                Name = "disconnect callback fires on peer close",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    TcpUdpLink client = new TcpUdpLink();
                    Collector hc = new Collector();
                    Collector cc = new Collector();
                    hc.Attach(host);
                    cc.Attach(client);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "connected");

                        client.Disconnect();

                        int waited = 0;
                        while (hc.Disconnects == 0 && waited < 3000)
                        {
                            host.Poll(4);
                            Thread.Sleep(10);
                            waited += 10;
                        }
                        Assert.True(hc.Disconnects >= 1, "host saw disconnect");
                        Assert.True(!host.IsConnected, "host not connected");
                    }
                    finally
                    {
                        client.Disconnect();
                        host.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "bounded input queue overflow disconnects",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink(1, 64 * 1024);
                    TcpUdpLink client = new TcpUdpLink();
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "connected");

                        client.SendReliable(MakeFrame(MessageKinds.Ping, 1, new byte[] { 1 }));
                        client.SendReliable(MakeFrame(MessageKinds.Ping, 2, new byte[] { 2 }));

                        int waited = 0;
                        while (!host.Overflowed && waited < 3000)
                        {
                            Thread.Sleep(10);
                            waited += 10;
                        }
                        Assert.True(host.Overflowed, "overflowed flag set");
                        Assert.True(!host.IsConnected, "link dropped after overflow");
                    }
                    finally
                    {
                        client.Disconnect();
                        host.Disconnect();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "host accepts replacement peer with fresh decoder and surviving IO threads",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    TcpUdpLink first = new TcpUdpLink();
                    TcpUdpLink replacement = new TcpUdpLink();
                    Collector hc = new Collector();
                    Collector rc = new Collector();
                    hc.Attach(host);
                    rc.Attach(replacement);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        first.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, first, 2000), "first connected");
                        first.SendReliable(MakeFrame(MessageKinds.Hello, 1, new byte[] { 11 }));
                        Assert.True(Drain(host, hc, 1, 3000), "first hello");
                        first.Disconnect();
                        int waited = 0;
                        while (hc.Disconnects == 0 && waited < 3000)
                        {
                            host.Poll(32);
                            Thread.Sleep(10);
                            waited += 10;
                        }
                        Assert.Equal(1, hc.Disconnects, "first disconnect delivered once");
                        replacement.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, replacement, 2000), "replacement connected");
                        replacement.SendReliable(MakeFrame(MessageKinds.Hello, 2, new byte[] { 22 }));
                        Assert.True(Drain(host, hc, 2, 3000), "replacement hello received");
                        host.SendReliable(MakeFrame(MessageKinds.Welcome, 3, new byte[] { 33 }));
                        Assert.True(Drain(replacement, rc, 1, 3000), "replacement welcome received");
                        Thread.Sleep(150);
                        replacement.SendReliable(MakeFrame(MessageKinds.Ping, 4, new byte[] { 44 }));
                        Assert.True(Drain(host, hc, 3, 3000), "old IO cannot close replacement");
                        Assert.Equal(1, hc.Disconnects, "no spurious replacement disconnect");
                    }
                    finally
                    {
                        first.Disconnect(); replacement.Disconnect(); host.Disconnect();
                    }
                }
            });
            s.Tests.Add(new TestCase
            {
                Name = "partial old TCP frame cannot contaminate replacement decoder",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink();
                    TcpUdpLink replacement = new TcpUdpLink();
                    Socket first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    Collector hc = new Collector(); hc.Attach(host);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        first.Connect(IPAddress.Loopback, HostTcpPort);
                        byte[] partial = FrameOnWire(MakeFrame(MessageKinds.Ping, 99, new byte[64]));
                        first.Send(partial, 0, 7, SocketFlags.None);
                        Thread.Sleep(50); first.Close();
                        int waited = 0;
                        while (hc.Disconnects == 0 && waited < 2000) { host.Poll(32); Thread.Sleep(10); waited += 10; }
                        Assert.Equal(1, hc.Disconnects, "old partial peer lost");
                        replacement.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, replacement, 2000), "replacement connected");
                        replacement.SendReliable(MakeFrame(MessageKinds.Hello, 1, new byte[] { 77 }));
                        Assert.True(Drain(host, hc, 1, 2000), "clean decoder accepts hello");
                        Assert.Equal((byte)77, PayloadOf(hc.Reliable[0])[0], "old partial bytes discarded");
                    }
                    finally { first.Close(); replacement.Disconnect(); host.Disconnect(); }
                }
            });
            s.Tests.Add(new TestCase
            {
                Name = "same client instance reconnects and authenticates new UDP port",
                Run = delegate
                {
                    TcpUdpLink host = new TcpUdpLink(); TcpUdpLink client = new TcpUdpLink();
                    Collector hc = new Collector(); Collector cc = new Collector(); hc.Attach(host); cc.Attach(client);
                    host.StartHost(FreeTcpPort(), FreeUdpPort());
                    try
                    {
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "first connection");
                        client.SendUnreliable(new byte[] { 1 });
                        Assert.True(DrainUdp(host, hc, 1), "first UDP endpoint learned");
                        host.ConfirmUnreliablePeer();
                        client.Disconnect();
                        int waited = 0;
                        while (hc.Disconnects == 0 && waited < 2000) { host.Poll(32); Thread.Sleep(10); waited += 10; }
                        Assert.Equal(1, hc.Disconnects, "old peer dropped");
                        client.Connect("127.0.0.1", HostTcpPort, HostUdpPort);
                        Assert.True(WaitConnected(host, client, 2000), "same instance reconnected");
                        client.SendUnreliable(new byte[] { 2 });
                        Assert.True(DrainUdp(host, hc, 2), "replacement UDP endpoint learned");
                        host.ConfirmUnreliablePeer();
                        host.SendUnreliable(new byte[] { 3 });
                        Assert.True(DrainUdp(client, cc, 1), "UDP returns to replacement endpoint");
                        Assert.Equal((byte)3, cc.Unreliable[0][0], "replacement received UDP reply");
                    }
                    finally { client.Disconnect(); host.Disconnect(); }
                }
            });
            return s;
        }

        // Ports used by the currently running pair; set by helpers before use.
        private static int HostTcpPort;
        private static int HostUdpPort;

        private sealed class Collector
        {
            public readonly List<byte[]> Reliable = new List<byte[]>();
            public readonly List<byte[]> Unreliable = new List<byte[]>();
            public int Disconnects;

            public void Attach(TcpUdpLink link)
            {
                link.OnFrame += delegate(byte[] bytes, bool reliable)
                {
                    lock (this)
                    {
                        if (reliable)
                        {
                            Reliable.Add(bytes);
                        }
                        else
                        {
                            Unreliable.Add(bytes);
                        }
                    }
                };
                link.OnDisconnected += delegate
                {
                    lock (this)
                    {
                        Disconnects++;
                    }
                };
            }
        }

        private static int FreeTcpPort()
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)s.LocalEndPoint).Port;
            s.Close();
            HostTcpPort = port;
            return port;
        }

        private static int FreeUdpPort()
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)s.LocalEndPoint).Port;
            s.Close();
            HostUdpPort = port;
            return port;
        }

        private static byte[] MakeFrame(ushort kind, ulong seq, byte[] payload)
        {
            FrameHeader h = new FrameHeader();
            h.Magic = ProtocolConstants.Magic;
            h.ProtocolVersion = ProtocolConstants.ProtocolVersion;
            h.MessageKind = kind;
            h.SessionId = new byte[16];
            h.ConnectionGeneration = 1;
            h.WorldEpoch = 0;
            h.MessageSeq = seq;
            h.PayloadLength = (uint)payload.Length;
            h.Flags = 0;
            h.Reserved = 0;

            byte[] header = HeaderCodec.Write(h);
            byte[] frame = new byte[header.Length + payload.Length];
            Array.Copy(header, 0, frame, 0, header.Length);
            Array.Copy(payload, 0, frame, header.Length, payload.Length);
            return frame;
        }

        private static byte[] PatternPayload(int size)
        {
            byte[] payload = new byte[size];
            for (int i = 0; i < size; i++)
            {
                payload[i] = (byte)(i & 0xFF);
            }
            return payload;
        }

        private static byte[] FrameOnWire(byte[] frame)
        {
            byte[] wire = new byte[4 + frame.Length];
            wire[0] = (byte)(frame.Length & 0xFF);
            wire[1] = (byte)((frame.Length >> 8) & 0xFF);
            wire[2] = (byte)((frame.Length >> 16) & 0xFF);
            wire[3] = (byte)((frame.Length >> 24) & 0xFF);
            Array.Copy(frame, 0, wire, 4, frame.Length);
            return wire;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] c = new byte[a.Length + b.Length];
            Array.Copy(a, 0, c, 0, a.Length);
            Array.Copy(b, 0, c, a.Length, b.Length);
            return c;
        }

        private static byte[] PayloadOf(byte[] frame)
        {
            FrameHeader h = HeaderCodec.Read(frame);
            byte[] payload = new byte[h.PayloadLength];
            Array.Copy(frame, ProtocolConstants.HeaderLength, payload, 0, payload.Length);
            return payload;
        }

        private static bool WaitConnected(TcpUdpLink a, TcpUdpLink b, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (a.IsConnected && b.IsConnected)
                {
                    return true;
                }
                Thread.Sleep(10);
                waited += 10;
            }
            return a.IsConnected && b.IsConnected;
        }

        private static bool Drain(TcpUdpLink link, Collector c, int wantReliable, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                link.Poll(32);
                lock (c)
                {
                    if (c.Reliable.Count >= wantReliable)
                    {
                        return true;
                    }
                }
                Thread.Sleep(10);
                waited += 10;
            }
            link.Poll(32);
            lock (c)
            {
                return c.Reliable.Count >= wantReliable;
            }
        }

        private static bool DrainUdp(TcpUdpLink link, Collector c, int count)
        {
            for (int waited = 0; waited < 2000; waited += 10)
            {
                link.Poll(32);
                if (c.Unreliable.Count >= count) return true;
                Thread.Sleep(10);
            }
            return false;
        }
    }
}
