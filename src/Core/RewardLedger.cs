using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public struct RewardGrantKey : IEquatable<RewardGrantKey>
    {
        public uint WorldEpoch;
        public int SourceKind;
        public ulong SourceKey;
        public uint RewardGeneration;
        public byte PlayerId;
        public int RewardKind;

        public RewardGrantKey(uint worldEpoch, int sourceKind, ulong sourceKey, uint rewardGeneration, byte playerId, int rewardKind)
        {
            WorldEpoch = worldEpoch;
            SourceKind = sourceKind;
            SourceKey = sourceKey;
            RewardGeneration = rewardGeneration;
            PlayerId = playerId;
            RewardKind = rewardKind;
        }

        public bool Equals(RewardGrantKey other)
        {
            return WorldEpoch == other.WorldEpoch
                && SourceKind == other.SourceKind
                && SourceKey == other.SourceKey
                && RewardGeneration == other.RewardGeneration
                && PlayerId == other.PlayerId
                && RewardKind == other.RewardKind;
        }

        public override bool Equals(object obj)
        {
            return obj is RewardGrantKey && Equals((RewardGrantKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)WorldEpoch;
                hash = (hash * 397) ^ SourceKind;
                hash = (hash * 397) ^ SourceKey.GetHashCode();
                hash = (hash * 397) ^ (int)RewardGeneration;
                hash = (hash * 397) ^ PlayerId;
                hash = (hash * 397) ^ RewardKind;
                return hash;
            }
        }
    }

    /// <summary>
    /// Grants each (epoch, source, generation, player, kind) exactly once.
    /// Prevents direct-coin + visual-pickup double counting and reconnect replays.
    /// </summary>
    public sealed class RewardLedger
    {
        private readonly Dictionary<RewardGrantKey, bool> granted = new Dictionary<RewardGrantKey, bool>();
        private readonly List<RewardGrantKey> order = new List<RewardGrantKey>();
        private readonly int capacity;

        public RewardLedger(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }
            this.capacity = capacity;
        }

        public int Count
        {
            get { return granted.Count; }
        }

        public bool IsGranted(RewardGrantKey key)
        {
            return granted.ContainsKey(key);
        }

        public bool TryGrant(RewardGrantKey key)
        {
            if (granted.ContainsKey(key))
            {
                return false;
            }
            granted.Add(key, true);
            order.Add(key);
            Trim();
            return true;
        }

        /// <summary>Drop grants older than the given epoch (world reset).</summary>
        public void PruneBeforeEpoch(uint epoch)
        {
            List<RewardGrantKey> toRemove = new List<RewardGrantKey>();
            foreach (RewardGrantKey key in order)
            {
                if (key.WorldEpoch < epoch)
                {
                    toRemove.Add(key);
                }
            }
            foreach (RewardGrantKey key in toRemove)
            {
                granted.Remove(key);
                order.Remove(key);
            }
        }

        private void Trim()
        {
            while (order.Count > capacity)
            {
                RewardGrantKey oldest = order[0];
                order.RemoveAt(0);
                granted.Remove(oldest);
            }
        }
    }
}
