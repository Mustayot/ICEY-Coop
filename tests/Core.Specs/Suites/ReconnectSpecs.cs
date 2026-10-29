using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ReconnectSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Reconnect";

            s.Tests.Add(new TestCase
            {
                Name = "issued ticket redeems once for the same session and generation",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(1000);
                    SessionId session = new SessionId(Fill(7));
                    byte[] ticket = tickets.Issue(PlayerIds.P2, session, 3, 100);
                    Assert.Equal(16, ticket.Length, "ticket is 16 bytes");
                    byte playerId;
                    string error;
                    Assert.True(tickets.TryRedeem(ticket, session, 3, 200, out playerId, out error), "first redeem");
                    Assert.Equal(PlayerIds.P2, playerId, "player id");
                    Assert.True(!tickets.TryRedeem(ticket, session, 3, 300, out playerId, out error), "second redeem refused");
                    Assert.Equal("unknown or already used ticket", error, "reuse reason");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "expired ticket is refused and purged",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(500);
                    SessionId session = new SessionId(Fill(1));
                    byte[] ticket = tickets.Issue(PlayerIds.P2, session, 1, 1000);
                    byte playerId;
                    string error;
                    Assert.True(!tickets.TryRedeem(ticket, session, 1, 2000, out playerId, out error), "expired refused");
                    Assert.Equal("ticket expired", error, "expiry reason");
                    Assert.Equal(0, tickets.Count, "expired entry dropped");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ticket ttl expires correctly across uint wrap",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(1000);
                    SessionId session = new SessionId(Fill(8));
                    uint issuedAt = uint.MaxValue - 500;
                    byte[] atBoundary = tickets.Issue(PlayerIds.P2, session, 1, issuedAt);
                    byte[] expired = tickets.Issue(PlayerIds.P2, session, 1, issuedAt);
                    byte playerId;
                    string error;

                    Assert.True(tickets.TryRedeem(atBoundary, session, 1, 499, out playerId, out error), "ticket valid at ttl boundary across wrap");
                    Assert.True(!tickets.TryRedeem(expired, session, 1, 500, out playerId, out error), "ticket expires one millisecond after ttl across wrap");
                    Assert.Equal("ticket expired", error, "wrapped expiry reason");
                    Assert.Equal(0, tickets.Count, "expired entry removed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ticket bytes are not the predictable counter and timestamp encoding",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(60000);
                    SessionId session = new SessionId(Fill(9));
                    byte[] issued = tickets.Issue(PlayerIds.P2, session, 1, 100);
                    byte[] predictable = new byte[ReconnectTickets.TicketLength];
                    uint a = 0x9E3779B9u;
                    uint b = a ^ unchecked(100u * 2654435761u);
                    for (int i = 0; i < 4; i++)
                    {
                        predictable[i] = unchecked((byte)(a >> (8 * i)));
                        predictable[4 + i] = unchecked((byte)(b >> (8 * i)));
                        predictable[8 + i] = unchecked((byte)((a ^ b) >> (8 * i)));
                        predictable[12 + i] = unchecked((byte)(unchecked(a + b) >> (8 * i)));
                    }
                    bool same = true;
                    for (int i = 0; i < issued.Length; i++)
                    {
                        if (issued[i] != predictable[i])
                        {
                            same = false;
                            break;
                        }
                    }
                    Assert.True(!same, "credential must not match the public counter/timestamp encoding");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ticket is bound to its session",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(5000);
                    SessionId mine = new SessionId(Fill(2));
                    SessionId other = new SessionId(Fill(3));
                    byte[] ticket = tickets.Issue(PlayerIds.P2, mine, 1, 0);
                    byte playerId;
                    string error;
                    Assert.True(!tickets.TryRedeem(ticket, other, 1, 10, out playerId, out error), "wrong session refused");
                    Assert.Equal("ticket belongs to another session", error, "session reason");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ticket is bound to its connection generation",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(5000);
                    SessionId session = new SessionId(Fill(4));
                    byte[] ticket = tickets.Issue(PlayerIds.P2, session, 2, 0);
                    byte playerId;
                    string error;
                    Assert.True(!tickets.TryRedeem(ticket, session, 3, 10, out playerId, out error), "stale generation refused");
                    Assert.Equal("ticket belongs to another connection generation", error, "generation reason");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "malformed tickets are rejected",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(5000);
                    SessionId session = new SessionId(Fill(5));
                    byte playerId;
                    string error;
                    Assert.True(!tickets.TryRedeem(null, session, 1, 0, out playerId, out error), "null ticket");
                    Assert.Equal("malformed ticket", error, "null reason");
                    Assert.True(!tickets.TryRedeem(new byte[8], session, 1, 0, out playerId, out error), "short ticket");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "purge and clear work",
                Run = delegate
                {
                    ReconnectTickets tickets = new ReconnectTickets(100);
                    SessionId session = new SessionId(Fill(6));
                    tickets.Issue(PlayerIds.P2, session, 1, 0);
                    tickets.Issue(PlayerIds.P2, session, 1, 0);
                    Assert.Equal(2, tickets.Count, "two issued");
                    Assert.Equal(2, tickets.PurgeExpired(1000), "both expired purged");
                    tickets.Issue(PlayerIds.P2, session, 1, 2000);
                    tickets.Clear();
                    Assert.Equal(0, tickets.Count, "cleared");
                }
            });

            return s;
        }

        private static byte[] Fill(byte value)
        {
            byte[] bytes = new byte[16];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(value + i);
            }
            return bytes;
        }
    }
}
