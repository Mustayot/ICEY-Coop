using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using IceyCoop.Core;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    public sealed class RelaySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Relay";

            s.Tests.Add(new TestCase
            {
                Name = "generated codes are well formed and unique",
                Run = delegate
                {
                    RoomRegistry registry = new RoomRegistry();
                    string error;
                    string first = registry.Create(1000, out error);
                    string second = registry.Create(1000, out error);
                    Assert.True(first != null && second != null, "codes created");
                    Assert.True(RoomRegistry.IsWellFormed(first), "first well formed");
                    Assert.True(RoomRegistry.IsWellFormed(second), "second well formed");
                    Assert.True(first != second, "codes differ");
                    Assert.Equal(2, registry.Count, "room count");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "join fills two slots then refuses",
                Run = delegate
                {
                    RoomRegistry registry = new RoomRegistry();
                    string error;
                    string code = registry.Create(1000, out error);
                    Assert.True(registry.TryJoin(code, 1100, out error), "first join");
                    Assert.True(registry.TryJoin(code, 1200, out error), "second join");
                    Assert.True(!registry.TryJoin(code, 1300, out error), "third join refused");
                    Assert.Equal("room full", error, "reason is room full");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "leaving frees the slot and empties the registry",
                Run = delegate
                {
                    RoomRegistry registry = new RoomRegistry();
                    string error;
                    string code = registry.Create(1000, out error);
                    registry.TryJoin(code, 1000, out error);
                    registry.Leave(code, 1100);
                    Assert.Equal(0, registry.Count, "empty room removed");
                    Assert.True(!registry.TryJoin(code, 1200, out error), "cannot join removed room");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "expired rooms purge and cannot be joined",
                Run = delegate
                {
                    RoomRegistry registry = new RoomRegistry(500);
                    string error;
                    string code = registry.Create(1000, out error);
                    Assert.Equal(0, registry.PurgeExpired(1200), "not expired yet");
                    Assert.Equal(1, registry.PurgeExpired(2000), "expired after ttl");
                    Assert.True(!registry.TryJoin(code, 2100, out error), "expired room cannot be joined");
                    Assert.Equal("unknown room", error, "reason is unknown room");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unknown and malformed codes are rejected",
                Run = delegate
                {
                    RoomRegistry registry = new RoomRegistry();
                    string error;
                    Assert.True(!registry.TryJoin("ZZZZZZ", 1000, out error), "unknown code");
                    Assert.Equal("unknown room", error, "unknown reason");
                    Assert.True(!RoomRegistry.IsWellFormed("abc"), "short code invalid");
                    Assert.True(!RoomRegistry.IsWellFormed("AAAAAA0"), "long code invalid");
                    Assert.True(!RoomRegistry.IsWellFormed("AAAAA0"), "look-alike zero rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "relay forwards bytes in both directions",
                Run = delegate
                {
                    RoomRegistry registry = new RoomRegistry();
                    string error;
                    string code = registry.Create(1000, out error);
                    registry.TryJoin(code, 1000, out error);
                    registry.TryJoin(code, 1000, out error);

                    RelayServer server = new RelayServer(registry);
                    TcpClient a = null;
                    TcpClient b = null;
                    try
                    {
                        server.Start(0);
                        a = Connect(server.Port);
                        SendCode(a, code);
                        b = Connect(server.Port);
                        SendCode(b, code);
                        Thread.Sleep(400);

                        Send(a, "ping");
                        Assert.Equal("ping", Receive(b, 4), "a to b");
                        Send(b, "pong");
                        Assert.Equal("pong", Receive(a, 4), "b to a");
                        Assert.Equal(1, server.PairedSessions, "one paired session");
                    }
                    finally
                    {
                        Close(a);
                        Close(b);
                        server.Dispose();
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "relay drops a connection with a malformed code",
                Run = delegate
                {
                    RelayServer server = new RelayServer(new RoomRegistry());
                    TcpClient bad = null;
                    try
                    {
                        server.Start(0);
                        bad = Connect(server.Port);
                        SendCode(bad, "000000");
                        Thread.Sleep(300);
                        Assert.True(!IsReadable(bad), "malformed code connection closed");
                        Assert.Equal(0, server.PairedSessions, "no session created");
                    }
                    finally
                    {
                        Close(bad);
                        server.Dispose();
                    }
                }
            });

            return s;
        }

        private static TcpClient Connect(int port)
        {
            TcpClient client = new TcpClient();
            client.NoDelay = true;
            client.Connect("127.0.0.1", port);
            return client;
        }

        private static void SendCode(TcpClient client, string code)
        {
            Send(client, code);
        }

        private static void Send(TcpClient client, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            client.GetStream().Write(bytes, 0, bytes.Length);
            client.GetStream().Flush();
        }

        private static string Receive(TcpClient client, int count)
        {
            client.ReceiveTimeout = 5000;
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = client.GetStream().Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    break;
                }
                offset += read;
            }
            return Encoding.ASCII.GetString(buffer, 0, offset);
        }

        private static bool IsReadable(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 500;
                byte[] buffer = new byte[1];
                int read = client.GetStream().Read(buffer, 0, 1);
                return read > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void Close(TcpClient client)
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
            }
        }
    }
}
