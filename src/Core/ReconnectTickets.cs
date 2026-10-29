using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace IceyCoop.Core
{
    /// <summary>
    /// Reconnect tickets (plan T22).
    ///
    /// A client that drops mid-session may come back once with the ticket it was
    /// issued. Rules that keep this safe:
    ///  - a ticket is bound to a session id AND a connection generation, so an old
    ///    ticket cannot ride into a newer room;
    ///  - single use: redeeming removes it;
    ///  - short TTL, because a reconnect that took a minute is a new join, not a
    ///    resume of the old state;
    ///  - a ticket never carries authority: the host still re-runs the scene gate
    ///    and baseline check after a successful redeem.
    ///
    /// Pure logic with an injected clock so the expiry and reuse rules are unit
    /// tested instead of discovered during a live disconnect.
    /// </summary>
    public sealed class ReconnectTickets
    {
        public const int TicketLength = 16;
        public const uint DefaultTtlMs = 60000;

        private sealed class Entry
        {
            public byte[] Ticket;
            public byte PlayerId;
            public byte[] SessionBytes;
            public uint Generation;
            public uint IssuedAtMs;
        }

        private readonly Dictionary<string, Entry> byTicket = new Dictionary<string, Entry>();
        private readonly uint ttlMs;

        public ReconnectTickets()
            : this(DefaultTtlMs)
        {
        }

        public ReconnectTickets(uint ticketTtlMs)
        {
            ttlMs = ticketTtlMs == 0 ? DefaultTtlMs : ticketTtlMs;
        }

        public int Count
        {
            get { return byTicket.Count; }
        }

        /// <summary>Issues a fresh ticket for a player in a specific session generation.</summary>
        public byte[] Issue(byte playerId, SessionId sessionId, uint generation, uint nowMs)
        {
            Entry entry = new Entry();
            entry.Ticket = new byte[TicketLength];
            entry.PlayerId = playerId;
            entry.SessionBytes = sessionId.ToArray();
            entry.Generation = generation;
            entry.IssuedAtMs = nowMs;

            RandomNumberGenerator random = RandomNumberGenerator.Create();
            try
            {
                do
                {
                    random.GetBytes(entry.Ticket);
                }
                while (byTicket.ContainsKey(KeyOf(entry.Ticket)));
            }
            finally
            {
                IDisposable disposable = random == null ? null : ((object)random) as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
            byTicket[KeyOf(entry.Ticket)] = entry;
            return (byte[])entry.Ticket.Clone();
        }

        /// <summary>
        /// Redeems a ticket. On success the ticket is consumed and the caller gets
        /// the player id; the session/generation must still be re-checked by the
        /// host before any authority is granted.
        /// </summary>
        public bool TryRedeem(byte[] ticket, SessionId sessionId, uint generation, uint nowMs,
            out byte playerId, out string error)
        {
            playerId = 0;
            error = null;
            if (ticket == null || ticket.Length != TicketLength)
            {
                error = "malformed ticket";
                return false;
            }
            string key = KeyOf(ticket);
            Entry entry;
            if (!byTicket.TryGetValue(key, out entry))
            {
                error = "unknown or already used ticket";
                return false;
            }
            if (IsExpired(entry, nowMs))
            {
                byTicket.Remove(key);
                error = "ticket expired";
                return false;
            }
            if (!SessionEquals(entry.SessionBytes, sessionId))
            {
                error = "ticket belongs to another session";
                return false;
            }
            if (entry.Generation != generation)
            {
                error = "ticket belongs to another connection generation";
                return false;
            }
            byTicket.Remove(key);
            playerId = entry.PlayerId;
            return true;
        }

        /// <summary>Drops expired entries; returns how many were removed.</summary>
        public int PurgeExpired(uint nowMs)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Entry> kv in byTicket)
            {
                if (IsExpired(kv.Value, nowMs))
                {
                    dead.Add(kv.Key);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                byTicket.Remove(dead[i]);
            }
            return dead.Count;
        }

        /// <summary>Revokes everything (room ended / new epoch).</summary>
        public void Clear()
        {
            byTicket.Clear();
        }

        private static bool SessionEquals(byte[] stored, SessionId sessionId)
        {
            if (stored == null || sessionId == null)
            {
                return false;
            }
            byte[] other = sessionId.ToArray();
            if (stored.Length != other.Length)
            {
                return false;
            }
            for (int i = 0; i < stored.Length; i++)
            {
                if (stored[i] != other[i])
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsExpired(Entry entry, uint nowMs)
        {
            return unchecked(nowMs - entry.IssuedAtMs) > ttlMs;
        }

        private static string KeyOf(byte[] ticket)
        {
            char[] chars = new char[ticket.Length * 2];
            const string hex = "0123456789ABCDEF";
            for (int i = 0; i < ticket.Length; i++)
            {
                chars[i * 2] = hex[(ticket[i] >> 4) & 0xF];
                chars[i * 2 + 1] = hex[ticket[i] & 0xF];
            }
            return new string(chars);
        }
    }
}
