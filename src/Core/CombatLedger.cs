using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public enum SettlementResult
    {
        Accepted = 0,
        Duplicate = 1,
        AlreadyPending = 2
    }

    /// <summary>
    /// Host-authoritative hit ledger. Replaces EnemyBaseHurt's single high-water
    /// mark: a key is accepted by identity (epoch, attacker, attack, target,
    /// ordinal), not by numeric attack-id order. Pending keys survive reentrant
    /// handlers; rejected contacts are released without settling.
    /// </summary>
    public sealed class CombatLedger
    {
        private readonly Dictionary<HitKey, ulong> settled = new Dictionary<HitKey, ulong>();
        private readonly HashSet<HitKey> pending = new HashSet<HitKey>();
        private readonly List<HitKey> settledOrder = new List<HitKey>();
        private readonly int capacity;

        public CombatLedger(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }
            this.capacity = capacity;
        }

        public int SettledCount
        {
            get { return settled.Count; }
        }

        public int PendingCount
        {
            get { return pending.Count; }
        }

        public bool IsSettled(HitKey key)
        {
            return settled.ContainsKey(key);
        }

        public bool IsPending(HitKey key)
        {
            return pending.Contains(key);
        }

        public SettlementResult BeginSettlement(HitKey key)
        {
            if (settled.ContainsKey(key))
            {
                return SettlementResult.Duplicate;
            }
            if (pending.Contains(key))
            {
                return SettlementResult.AlreadyPending;
            }
            pending.Add(key);
            return SettlementResult.Accepted;
        }

        public void CommitSettlement(HitKey key, ulong revision)
        {
            if (!pending.Contains(key))
            {
                throw new InvalidOperationException("commit without begin");
            }
            pending.Remove(key);
            settled[key] = revision;
            settledOrder.Add(key);
            Trim();
        }

        public void ReleaseSettlement(HitKey key)
        {
            pending.Remove(key);
        }

        public bool TryGetRevision(HitKey key, out ulong revision)
        {
            return settled.TryGetValue(key, out revision);
        }

        private void Trim()
        {
            while (settledOrder.Count > capacity)
            {
                HitKey oldest = settledOrder[0];
                settledOrder.RemoveAt(0);
                settled.Remove(oldest);
            }
        }
    }
}
