using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ResumeGateSpecs
    {
        private static byte[] TicketFor(ReconnectTickets tickets, SessionId id, uint generation, uint nowMs)
        {
            return tickets.Issue(2, id, generation, nowMs);
        }

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ResumeGate";

            s.Tests.Add(new TestCase
            {
                Name = "a good ticket inside the window resumes",
                Run = delegate
                {
                    SessionId id = new SessionId(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                    ReconnectTickets tickets = new ReconnectTickets(60000);
                    ReconnectWindow window = new ReconnectWindow(20000);
                    window.Occupy(0);
                    uint oldGeneration = window.Generation;
                    byte[] ticket = TicketFor(tickets, id, oldGeneration, 0);
                    window.PeerLost(1000);

                    uint gen;
                    ResumeResult r = ResumeGate.TryResume(window, tickets, ticket, id, 5000, out gen);
                    Assert.Equal(ResumeResult.Resumed, r, "resumed");
                    Assert.Equal(oldGeneration + 1, gen, "generation increments once");
                    Assert.Equal(gen, window.Generation, "reported generation committed");
                    Assert.Equal(SlotState.Occupied, window.State, "slot held again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a released slot cannot be resumed even with a good ticket",
                Run = delegate
                {
                    SessionId id = new SessionId(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                    ReconnectTickets tickets = new ReconnectTickets(60000);
                    ReconnectWindow window = new ReconnectWindow(1000);
                    window.Occupy(0);
                    byte[] ticket = TicketFor(tickets, id, window.Generation, 0);
                    window.PeerLost(100);
                    window.Poll(5000);   // window expires

                    uint gen;
                    ResumeResult r = ResumeGate.TryResume(window, tickets, ticket, id, 6000, out gen);
                    Assert.Equal(ResumeResult.NotInGrace, r, "no grace left");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a bad ticket keeps the window open for a real retry",
                Run = delegate
                {
                    SessionId id = new SessionId(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                    ReconnectTickets tickets = new ReconnectTickets(60000);
                    ReconnectWindow window = new ReconnectWindow(20000);
                    window.Occupy(0);
                    byte[] validTicket = TicketFor(tickets, id, window.Generation, 0);
                    window.PeerLost(1000);
                    uint originalGeneration = window.Generation;

                    uint gen;
                    ResumeResult r = ResumeGate.TryResume(window, tickets, null, id, 2000, out gen);
                    Assert.Equal(ResumeResult.BadTicket, r, "bad ticket");
                    Assert.Equal(SlotState.Grace, window.State, "still in grace");
                    Assert.Equal(originalGeneration, window.Generation, "failed attempt preserves generation");
                    Assert.Equal((uint)19000, window.RemainingMs(2000), "failed attempt preserves original deadline");

                    r = ResumeGate.TryResume(window, tickets, validTicket, id, 3000, out gen);
                    Assert.Equal(ResumeResult.Resumed, r, "original valid ticket still works");
                    Assert.Equal(originalGeneration + 1, gen, "successful retry increments once");
                    Assert.Equal(gen, window.Generation, "retry generation committed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "wrong session is bad ticket and leaves the original ticket usable",
                Run = delegate
                {
                    SessionId id = new SessionId(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                    SessionId otherId = new SessionId(new byte[] { 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });
                    ReconnectTickets tickets = new ReconnectTickets(60000);
                    ReconnectWindow window = new ReconnectWindow(20000);
                    window.Occupy(0);
                    byte[] ticket = TicketFor(tickets, id, window.Generation, 0);
                    window.PeerLost(1000);
                    uint oldGeneration = window.Generation;

                    uint gen;
                    ResumeResult result = ResumeGate.TryResume(window, tickets, ticket, otherId, 2000, out gen);
                    Assert.Equal(ResumeResult.BadTicket, result, "wrong session classification");
                    Assert.Equal(oldGeneration, window.Generation, "wrong session preserves generation");
                    Assert.Equal((uint)19000, window.RemainingMs(2000), "wrong session preserves deadline");
                    Assert.Equal(ResumeResult.Resumed, ResumeGate.TryResume(window, tickets, ticket, id, 3000, out gen), "ticket remains usable");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "expired ticket is classified without extending grace",
                Run = delegate
                {
                    SessionId id = new SessionId(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                    ReconnectTickets tickets = new ReconnectTickets(1000);
                    ReconnectWindow window = new ReconnectWindow(20000);
                    window.Occupy(0);
                    byte[] ticket = TicketFor(tickets, id, window.Generation, 0);
                    window.PeerLost(1000);
                    uint oldGeneration = window.Generation;

                    uint gen;
                    ResumeResult result = ResumeGate.TryResume(window, tickets, ticket, id, 2001, out gen);
                    Assert.Equal(ResumeResult.Expired, result, "ticket expiry classification");
                    Assert.Equal(SlotState.Grace, window.State, "expired credential does not release slot");
                    Assert.Equal(oldGeneration, window.Generation, "expired credential preserves generation");
                    Assert.Equal((uint)18999, window.RemainingMs(2001), "expired credential preserves deadline");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a ticket cannot be replayed",
                Run = delegate
                {
                    SessionId id = new SessionId(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
                    ReconnectTickets tickets = new ReconnectTickets(60000);
                    ReconnectWindow window = new ReconnectWindow(20000);
                    window.Occupy(0);
                    byte[] ticket = TicketFor(tickets, id, window.Generation, 0);
                    window.PeerLost(1000);

                    uint gen;
                    Assert.Equal(ResumeResult.Resumed, ResumeGate.TryResume(window, tickets, ticket, id, 2000, out gen), "first use");
                    uint resumedGeneration = window.Generation;
                    window.PeerLost(2100);
                    ResumeResult second = ResumeGate.TryResume(window, tickets, ticket, id, 2200, out gen);
                    Assert.Equal(ResumeResult.BadTicket, second, "replay classified as bad ticket");
                    Assert.Equal(resumedGeneration, window.Generation, "replay preserves generation");
                    Assert.Equal((uint)19900, window.RemainingMs(2200), "replay does not extend grace");
                }
            });

            return s;
        }
    }
}
