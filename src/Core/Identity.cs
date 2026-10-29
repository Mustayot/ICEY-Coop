using System;

namespace IceyCoop.Core
{
    public enum LifeState : byte
    {
        Alive = 1,
        Downed = 2
    }

    public enum ConnectionState : byte
    {
        Disconnected = 0,
        Joining = 1,
        Active = 2,
        Suspended = 3
    }

    /// <summary>16-byte session identity. Pure; no Unity/Guid dependence.</summary>
    public sealed class SessionId
    {
        public static readonly SessionId Zero = new SessionId(new byte[16]);

        private readonly byte[] bytes;

        public SessionId(byte[] value)
        {
            if (value == null || value.Length != 16)
            {
                throw new ArgumentException("SessionId must be exactly 16 bytes.", "value");
            }
            bytes = new byte[16];
            Array.Copy(value, bytes, 16);
        }

        public byte[] ToArray()
        {
            byte[] copy = new byte[16];
            Array.Copy(bytes, copy, 16);
            return copy;
        }

        public bool IsZero()
        {
            for (int i = 0; i < 16; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }
            }
            return true;
        }
    }

    public static class PlayerIds
    {
        public const byte P1 = 1;
        public const byte P2 = 2;

        public static bool IsValid(byte id)
        {
            return id == P1 || id == P2;
        }
    }

    /// <summary>Unique key for one authoritative hit settlement.</summary>
    public struct HitKey : IEquatable<HitKey>
    {
        public uint WorldEpoch;
        public ulong AttackerEntityId;
        public ulong AttackSeq;
        public ulong TargetEntityId;
        public uint HitOrdinal;

        public HitKey(uint worldEpoch, ulong attackerEntityId, ulong attackSeq, ulong targetEntityId, uint hitOrdinal)
        {
            WorldEpoch = worldEpoch;
            AttackerEntityId = attackerEntityId;
            AttackSeq = attackSeq;
            TargetEntityId = targetEntityId;
            HitOrdinal = hitOrdinal;
        }

        public bool Equals(HitKey other)
        {
            return WorldEpoch == other.WorldEpoch
                && AttackerEntityId == other.AttackerEntityId
                && AttackSeq == other.AttackSeq
                && TargetEntityId == other.TargetEntityId
                && HitOrdinal == other.HitOrdinal;
        }

        public override bool Equals(object obj)
        {
            return obj is HitKey && Equals((HitKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)WorldEpoch;
                hash = (hash * 397) ^ AttackerEntityId.GetHashCode();
                hash = (hash * 397) ^ AttackSeq.GetHashCode();
                hash = (hash * 397) ^ TargetEntityId.GetHashCode();
                hash = (hash * 397) ^ (int)HitOrdinal;
                return hash;
            }
        }
    }
}
