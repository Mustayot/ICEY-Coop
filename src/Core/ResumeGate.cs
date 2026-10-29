using System;

namespace IceyCoop.Core
{
    public enum ResumeResult
    {
        /// <summary>Ticket accepted: continue in the same session, new generation.</summary>
        Resumed = 0,
        /// <summary>The slot is not in its grace window (already released or never lost).</summary>
        NotInGrace = 1,
        /// <summary>No ticket, or a ticket the host never issued for this session.</summary>
        BadTicket = 2,
        /// <summary>Ticket matched but has aged out.</summary>
        Expired = 3
    }

    /// <summary>
    /// T22 gate for "come back with a ticket". Combines the two halves that decide a resume -
    /// the grace window (ReconnectWindow) and the ticket ledger (ReconnectTickets) - so the
    /// decision is one call and cannot drift between the two.
    ///
    /// Order matters: the window is checked without side effects first, then the ticket is
    /// redeemed (single use, bound to session and generation), and only then is the window
    /// committed to the new generation.
    /// </summary>
    public static class ResumeGate
    {
        public static ResumeResult TryResume(ReconnectWindow window, ReconnectTickets tickets,
            byte[] ticket, SessionId sessionId, uint nowMs, out uint newGeneration)
        {
            newGeneration = 0;
            if (window == null) throw new ArgumentNullException("window");
            if (window.State != SlotState.Grace)
            {
                return ResumeResult.NotInGrace;
            }
            if (!window.CanResume(nowMs))
            {
                return ResumeResult.Expired;
            }
            if (tickets == null || ticket == null || ticket.Length != ReconnectTickets.TicketLength)
            {
                return ResumeResult.BadTicket;
            }
            if (window.Generation == uint.MaxValue)
            {
                throw new InvalidOperationException("Connection generation exhausted.");
            }

            // The ticket was issued for the generation the peer left on; committing the resume
            // bumps the window generation only after successful redemption.
            uint expectedGeneration = window.Generation;
            byte playerId;
            string why;
            if (!tickets.TryRedeem(ticket, sessionId, expectedGeneration, nowMs, out playerId, out why))
            {
                return string.Equals(why, "ticket expired", StringComparison.Ordinal)
                    ? ResumeResult.Expired
                    : ResumeResult.BadTicket;
            }
            if (!window.TryResume(nowMs))
            {
                throw new InvalidOperationException("Reconnect window changed during ticket redemption.");
            }
            newGeneration = window.Generation;
            return ResumeResult.Resumed;
        }
    }
}
