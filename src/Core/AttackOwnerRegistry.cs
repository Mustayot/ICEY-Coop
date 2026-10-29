using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>
    /// Links attack instances to their owning entity.
    ///
    /// An attack instance is identified by the composite key (ownerEntityId, attackSeq):
    /// sequences are allocated monotonically per attacker, so two different attackers can
    /// both have sequence 1 without colliding. This fixes the previous defect where the
    /// global attack sequence number was the sole dictionary key, so a second player's
    /// first attack could not be registered because another owner already held sequence 1.
    ///
    /// Scope note: this fix only resolves ownership conflicts. Feeding the enemy hurtId
    /// high-water mark into CombatLedger is a later batch; do not claim multi-player
    /// damage accounting is complete yet.
    /// </summary>
    public sealed class AttackOwnerRegistry
    {
        private struct AttackRef : IEquatable<AttackRef>
        {
            public ulong OwnerEntityId;
            public ulong AttackSeq;

            public AttackRef(ulong ownerEntityId, ulong attackSeq)
            {
                OwnerEntityId = ownerEntityId;
                AttackSeq = attackSeq;
            }

            public bool Equals(AttackRef other)
            {
                return OwnerEntityId == other.OwnerEntityId && AttackSeq == other.AttackSeq;
            }

            public override bool Equals(object obj)
            {
                return obj is AttackRef && Equals((AttackRef)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + OwnerEntityId.GetHashCode();
                    hash = hash * 31 + AttackSeq.GetHashCode();
                    return hash;
                }
            }
        }

        private readonly Dictionary<AttackRef, ulong> attackOwner = new Dictionary<AttackRef, ulong>();
        private readonly Dictionary<ulong, ulong> nextSequence = new Dictionary<ulong, ulong>();
        private readonly List<AttackRef> order = new List<AttackRef>();
        private readonly int capacity;

        public AttackOwnerRegistry(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }
            this.capacity = capacity;
        }

        public int Count
        {
            get { return attackOwner.Count; }
        }

        /// <summary>
        /// Allocates the next sequence number for the given owner. Sequences stay
        /// monotonically increasing per owner, independent of other owners.
        /// </summary>
        public ulong AllocateAttack(ulong ownerEntityId)
        {
            ulong next;
            if (!nextSequence.TryGetValue(ownerEntityId, out next))
            {
                next = 1;
            }
            else
            {
                if (next == ulong.MaxValue)
                {
                    throw new InvalidOperationException("attack sequence overflow");
                }
                next = next + 1;
            }
            nextSequence[ownerEntityId] = next;
            return next;
        }

        /// <summary>
        /// Registers an attack instance under the composite key (ownerEntityId, attackSeq).
        /// An already-registered identical key is idempotent (returns true); a different
        /// owner using the same sequence number is a different key and coexists.
        /// </summary>
        public bool RegisterAttack(ulong ownerEntityId, ulong attackSeq)
        {
            AttackRef key = new AttackRef(ownerEntityId, attackSeq);
            ulong existingOwner;
            if (attackOwner.TryGetValue(key, out existingOwner))
            {
                return existingOwner == ownerEntityId;
            }
            attackOwner[key] = ownerEntityId;
            order.Add(key);
            Trim();
            return true;
        }

        public bool TryGetOwner(ulong ownerEntityId, ulong attackSeq, out ulong owner)
        {
            AttackRef key = new AttackRef(ownerEntityId, attackSeq);
            return attackOwner.TryGetValue(key, out owner);
        }

        public bool ReleaseAttack(ulong ownerEntityId, ulong attackSeq)
        {
            AttackRef key = new AttackRef(ownerEntityId, attackSeq);
            return attackOwner.Remove(key);
        }

        private void Trim()
        {
            while (order.Count > capacity)
            {
                AttackRef oldest = order[0];
                order.RemoveAt(0);
                attackOwner.Remove(oldest);
            }
        }
    }
}
