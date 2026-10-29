using System;
using System.Collections.Generic;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    /// <summary>
    /// T23: the relay link against a real RelayServer on a real socket. These are the claims the
    /// public-network path rests on - both ends dial the same room, bytes cross unmodified, the
    /// reliable/unreliable channel survives the trip, and a different room code never sees them.
    /// </summary>
    public sealed class RelayLinkSpecs
    {
        private static readonly List<RelayLink> pumped = new List<RelayLink>();

        /// <summary>
        /// Frames only reach a link's handler from Poll (the same contract the session loop obeys),
        /// so every wait pumps the links it is waiting on.
        /// </summary>
        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < pumped.Count; i++)
                {
                    try { pumped[i].Poll(64); } catch { }
                }
                if (condition()) return true;
                System.Threading.Thread.Sleep(10);
            }
            for (int i = 0; i < pumped.Count; i++)
            {
                try { pumped[i].Poll(64); } catch { }
            }
            return condition();
        }

        private sealed class Sink
        {
            public readonly List<byte[]> Reliable = new List<byte[]>();
            public readonly List<byte[]> Unreliable = new List<byte[]>();
            public readonly List<string> Diagnostics = new List<string>();

            public Sink(RelayLink link)
            {
                link.OnFrame += delegate(byte[] payload, bool reliable)
                {
                    if (reliable) Reliable.Add(payload);
                    else Unreliable.Add(payload);
                };
                link.OnDiagnostic += delegate(string message) { Diagnostics.Add(message); };
            }

            public int Total { get { return Reliable.Count + Unreliable.Count; } }
        }

        private static RelayLink Pair(RelayServer server, string code, out Sink sink)
        {
            RelayLink link = new RelayLink("127.0.0.1", server.Port, code);
            sink = new Sink(link);
            link.StartHost(0, 0);
            pumped.Add(link);
            return link;
        }

        /// <summary>The client role: dials the same room, but reports connected as soon as it dials.</summary>
        private static RelayLink PairClient(RelayServer server, string code, out Sink sink)
        {
            RelayLink link = new RelayLink("127.0.0.1", server.Port, code);
            sink = new Sink(link);
            link.Connect("127.0.0.1", 0, 0);
            pumped.Add(link);
            return link;
        }

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "RelayLink";

            s.Tests.Add(new TestCase
            {
                Name = "two peers on the same room code exchange reliable frames through the relay",
                Run = delegate
                {
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        Sink clientSink;
                        RelayLink host = Pair(server, "ABC234", out hostSink);
                        RelayLink client = Pair(server, "ABC234", out clientSink);
                        try
                        {
                            Assert.True(WaitFor(delegate { return server.PairedSessions == 1; }, 3000), "the relay paired the two peers");
                            host.SendReliable(new byte[] { 1, 2, 3, 4 });
                            client.SendReliable(new byte[] { 9, 8, 7 });
                            Assert.True(WaitFor(delegate { return clientSink.Reliable.Count == 1 && hostSink.Reliable.Count == 1; }, 3000),
                                "both directions delivered (host=" + hostSink.Reliable.Count + " client=" + clientSink.Reliable.Count + ")");
                            Assert.True(clientSink.Reliable[0].Length == 4 && clientSink.Reliable[0][2] == 3, "payload crossed unmodified");
                            Assert.True(hostSink.Reliable[0].Length == 3 && hostSink.Reliable[0][0] == 9, "payload crossed the other way too");
                        }
                        finally
                        {
                            host.Disconnect();
                            client.Disconnect();
                        }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the unreliable channel keeps its tag over a single stream",
                Run = delegate
                {
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        Sink clientSink;
                        RelayLink host = Pair(server, "DEF567", out hostSink);
                        RelayLink client = Pair(server, "DEF567", out clientSink);
                        try
                        {
                            Assert.True(WaitFor(delegate { return server.PairedSessions == 1; }, 3000), "paired");
                            host.SendUnreliable(new byte[] { 42 });
                            host.SendReliable(new byte[] { 43 });
                            Assert.True(WaitFor(delegate { return clientSink.Total == 2; }, 3000),
                                "both frames arrived (got " + clientSink.Total + ")");
                            Assert.Equal(1, clientSink.Unreliable.Count, "exactly one unreliable frame");
                            Assert.Equal(1, clientSink.Reliable.Count, "exactly one reliable frame");
                            Assert.Equal((byte)42, clientSink.Unreliable[0][0], "unreliable payload intact");
                            Assert.Equal((byte)43, clientSink.Reliable[0][0], "reliable payload intact");
                        }
                        finally
                        {
                            host.Disconnect();
                            client.Disconnect();
                        }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reliable frames keep their send order across the relay",
                Run = delegate
                {
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        Sink clientSink;
                        RelayLink host = Pair(server, "GHJ892", out hostSink);
                        RelayLink client = Pair(server, "GHJ892", out clientSink);
                        try
                        {
                            Assert.True(WaitFor(delegate { return server.PairedSessions == 1; }, 3000), "paired");
                            for (int i = 0; i < 40; i++)
                            {
                                host.SendReliable(new byte[] { (byte)i });
                            }
                            Assert.True(WaitFor(delegate { return clientSink.Reliable.Count == 40; }, 5000),
                                "all forty frames arrived (got " + clientSink.Reliable.Count + ")");
                            bool ordered = true;
                            for (int i = 0; i < 40; i++)
                            {
                                if (clientSink.Reliable[i][0] != (byte)i) { ordered = false; break; }
                            }
                            Assert.True(ordered, "frames arrived in send order");
                        }
                        finally
                        {
                            host.Disconnect();
                            client.Disconnect();
                        }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a different room code never receives the other pair's traffic",
                Run = delegate
                {
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        Sink clientSink;
                        Sink strangerSink;
                        RelayLink host = Pair(server, "MNP345", out hostSink);
                        RelayLink client = Pair(server, "MNP345", out clientSink);
                        RelayLink stranger = Pair(server, "RST678", out strangerSink);
                        try
                        {
                            Assert.True(WaitFor(delegate { return server.PairedSessions == 1; }, 3000), "the room paired");
                            host.SendReliable(new byte[] { 5, 5, 5 });
                            Assert.True(WaitFor(delegate { return clientSink.Reliable.Count == 1; }, 3000), "the room partner received it");
                            System.Threading.Thread.Sleep(300);
                            Assert.Equal(0, strangerSink.Total, "the lone stranger received nothing");
                            Assert.Equal(1, server.PairedSessions, "a lone peer never counts as a paired session");
                        }
                        finally
                        {
                            host.Disconnect();
                            client.Disconnect();
                            stranger.Disconnect();
                        }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a malformed room code is refused before any socket is opened",
                Run = delegate
                {
                    bool refused = false;
                    try { new RelayLink("127.0.0.1", 1234, "abc234"); }
                    catch (ArgumentException) { refused = true; }
                    Assert.True(refused, "lowercase code refused");
                    refused = false;
                    try { new RelayLink("127.0.0.1", 1234, "ABCDE0"); }
                    catch (ArgumentException) { refused = true; }
                    Assert.True(refused, "look-alike zero refused");
                    refused = false;
                    try { new RelayLink("127.0.0.1", 0, "ABC234"); }
                    catch (ArgumentOutOfRangeException) { refused = true; }
                    Assert.True(refused, "bad port refused");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a disconnect surfaces as OnDisconnected instead of a silent hang",
                Run = delegate
                {
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        Sink clientSink;
                        RelayLink host = Pair(server, "TUV234", out hostSink);
                        RelayLink client = Pair(server, "TUV234", out clientSink);
                        bool dropped = false;
                        client.OnDisconnected += delegate { dropped = true; };
                        try
                        {
                            Assert.True(WaitFor(delegate { return server.PairedSessions == 1; }, 3000), "paired");
                            host.Disconnect();
                            Assert.True(WaitFor(delegate { return dropped; }, 5000), "the surviving peer was told the pair ended");
                            Assert.True(!client.IsConnected, "the link no longer claims a live socket");
                        }
                        finally
                        {
                            host.Disconnect();
                            client.Disconnect();
                        }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a lone host does not claim a peer until the partner speaks",
                Run = delegate
                {
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        RelayLink host = Pair(server, "WXY789", out hostSink);
                        try
                        {
                            // The relay accepted our socket, but nobody is in the room yet. The host
                            // must not report a peer: the session layer drops a peer that stays quiet
                            // for five seconds, so a false "connected" would time itself out alone.
                            System.Threading.Thread.Sleep(400);
                            Assert.True(!host.IsConnected, "a lone host is not connected");
                            Assert.True(!host.PeerHeard, "a lone host has heard nobody");

                            Sink clientSink;
                            RelayLink client = PairClient(server, "WXY789", out clientSink);
                            try
                            {
                                Assert.True(client.IsConnected, "the client is connected after dialing");
                                client.SendReliable(new byte[] { 7 });
                                Assert.True(WaitFor(delegate { return host.IsConnected; }, 3000),
                                    "the host notices the partner once its bytes arrive");
                                Assert.True(host.PeerHeard, "peer heard flag set");
                                // PeerHeard is set on the socket thread; OnFrame is
                                // delivered by Poll. Wait for that separate boundary.
                                Assert.True(WaitFor(delegate { return hostSink.Reliable.Count == 1; }, 3000),
                                    "the partner frame is dispatched to the host callback");
                                Assert.Equal(1, hostSink.Reliable.Count, "and the frame itself arrived");
                            }
                            finally { client.Disconnect(); }
                        }
                        finally { host.Disconnect(); }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a pair that goes quiet for six seconds is not torn down",
                Run = delegate
                {
                    // The regression this guards: the relay armed a 5s receive timeout to read the
                    // room code and left it on the live socket, so the first five quiet seconds of a
                    // real session (a scene load) closed both peers. Silence is normal.
                    using (RelayServer server = new RelayServer())
                    {
                        server.Start(0);
                        Sink hostSink;
                        Sink clientSink;
                        RelayLink host = Pair(server, "KMQ234", out hostSink);
                        RelayLink client = PairClient(server, "KMQ234", out clientSink);
                        try
                        {
                            Assert.True(WaitFor(delegate { return server.PairedSessions == 1; }, 3000), "paired");
                            // The client always speaks first in a real session (its Hello), which is
                            // also how the host learns a partner exists on a relay.
                            client.SendReliable(new byte[] { 1 });
                            Assert.True(WaitFor(delegate { return hostSink.Reliable.Count == 1; }, 3000), "first frame arrived");

                            // Six seconds of nothing on the wire.
                            DateTime quietUntil = DateTime.UtcNow.AddSeconds(6);
                            while (DateTime.UtcNow < quietUntil)
                            {
                                host.Poll(64);
                                client.Poll(64);
                                System.Threading.Thread.Sleep(20);
                            }
                            Assert.True(host.IsConnected, "the host still considers the pair alive after the quiet period [" + string.Join(" | ", hostSink.Diagnostics.ToArray()) + "] queued=" + host.QueuedFrames);
                            Assert.True(client.IsConnected, "so does the client [" + string.Join(" | ", clientSink.Diagnostics.ToArray()) + "]");

                            host.SendReliable(new byte[] { 2 });
                            Assert.True(WaitFor(delegate { return clientSink.Reliable.Count == 1; }, 3000),
                                "the pair still carries frames after the quiet period (got "
                                + clientSink.Reliable.Count + " of 1)");
                            Assert.Equal((byte)2, clientSink.Reliable[0][0], "payload intact after the quiet period");
                        }
                        finally
                        {
                            host.Disconnect();
                            client.Disconnect();
                        }
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a relay target is parsed strictly, never repaired",
                Run = delegate
                {
                    RelayTarget target;
                    string error;
                    Assert.True(RelayTarget.TryParse("ABC234@relay.example.com:57400", out target, out error),
                        "full target accepted: " + error);
                    Assert.Equal("ABC234", target.Code, "room code");
                    Assert.Equal("relay.example.com", target.Host, "relay host");
                    Assert.Equal(57400, target.Port, "relay port");

                    Assert.True(RelayTarget.TryParse("ABC234@10.1.2.3", out target, out error), "ipv4 without a port accepted");
                    Assert.Equal(RelayTarget.DefaultPort, target.Port, "default port filled in");

                    Assert.True(!RelayTarget.TryParse("ABC234", out target, out error), "a bare code is refused");
                    Assert.True(!RelayTarget.TryParse("@127.0.0.1:1", out target, out error), "a missing code is refused");
                    Assert.True(!RelayTarget.TryParse("abc234@127.0.0.1:57400", out target, out error), "a lowercase code is refused");
                    Assert.True(!RelayTarget.TryParse("ABC234@127.0.0.1:0", out target, out error), "port zero is refused");
                    Assert.True(!RelayTarget.TryParse("ABC234@127.0.0.1:99999", out target, out error), "port above 65535 is refused");
                    Assert.True(!RelayTarget.TryParse("ABC234@tcp://127.0.0.1:57400", out target, out error), "a scheme is refused");
                }
            });

            return s;
        }
    }
}
