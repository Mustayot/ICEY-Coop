using System;
using System.Collections.Generic;

namespace IceyCoop.Protocol
{
    /// <summary>
    /// Input record for SnapshotAssembler. The GameBridge host adapter maps
    /// EntityRegistry records + actor model + authoritative bindings into these
    /// plain DTOs, so the assembler itself has no dependency on GameBridge or
    /// Core types. EntityId/Kind/Revision line up with Core.EntityRegistry.
    /// EntityRecord and Protocol EntitySnapshot.
    /// </summary>
    public struct EntitySource
    {
        public ulong EntityId;
        public int Kind;
        public ulong Revision;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public int Facing;
        public uint AnimId;
        public float AnimProgress;
        public int Hp;
        public int Shield;
        public byte LifeState; // LifeState enum byte
        public ulong TargetEntityId;
        /// <summary>Authoritative prefab key (see EntitySnapshot.PrefabKey).</summary>
        public string PrefabKey;
    }

    /// <summary>
    /// Host-side immutable snapshot assembly. Build converts a set of entity
    /// sources into a deterministically ordered EntitySnapshot list; Slice cuts
    /// that list into parts where each entity record stays whole (never split
    /// across parts) and each part's encoded entity body stays within a byte
    /// budget. No randomness, fixed field order: the same input yields the same
    /// bytes every time.
    /// </summary>
    public sealed class SnapshotAssembler
    {
        // 03 contract: ordinary UDP datagrams stay under 1200 bytes; reliable
        // baseline slices are recommended at 32 KiB. These are the entity-body
        // budgets passed to Slice; callers subtract the fixed StateSnapshot
        // header overhead from the total datagram budget themselves.
        public const int UdpSnapshotMaxBytes = 1200;
        public const int ReliableSliceMaxBytes = 32 * 1024;

        public ulong IncludedEventSeq;
        public ulong HostTick;

        public List<EntitySnapshot> Build(IEnumerable<EntitySource> sources)
        {
            List<EntitySnapshot> list = new List<EntitySnapshot>();
            if (sources != null)
            {
                foreach (EntitySource src in sources)
                {
                    EntitySnapshot snap = new EntitySnapshot();
                    snap.EntityId = src.EntityId;
                    snap.Kind = src.Kind;
                    snap.Revision = src.Revision;
                    snap.X = src.X;
                    snap.Y = src.Y;
                    snap.Vx = src.Vx;
                    snap.Vy = src.Vy;
                    snap.Facing = src.Facing;
                    snap.AnimId = src.AnimId;
                    snap.AnimProgress = src.AnimProgress;
                    snap.Hp = src.Hp;
                    snap.Shield = src.Shield;
                    snap.LifeState = src.LifeState;
                    snap.TargetEntityId = src.TargetEntityId;
                    snap.PrefabKey = src.PrefabKey;
                    list.Add(snap);
                }
            }

            // Deterministic order: ascending EntityId, stable regardless of
            // source enumeration order.
            list.Sort(delegate(EntitySnapshot a, EntitySnapshot b)
            {
                return a.EntityId.CompareTo(b.EntityId);
            });
            return list;
        }

        /// <summary>
        /// Splits a built snapshot into whole-entity parts. Every entity record
        /// lands whole in exactly one part, and each part's encoded entity body
        /// (uint16 length prefixes + records) stays at or below maxBytes. An
        /// empty snapshot yields one empty part so a StateSnapshot always has
        /// PartCount >= 1.
        /// </summary>
        public List<EntitySnapshot[]> Slice(IList<EntitySnapshot> entities, int maxBytes)
        {
            if (maxBytes < 1)
            {
                throw new ProtocolException("slice budget must be positive");
            }

            List<EntitySnapshot[]> parts = new List<EntitySnapshot[]>();
            List<EntitySnapshot> current = new List<EntitySnapshot>();
            int currentBytes = 0;

            int count = entities == null ? 0 : entities.Count;
            for (int i = 0; i < count; i++)
            {
                EntitySnapshot snap = entities[i];
                int recordBytes = RecordBytes(snap);
                if (recordBytes > maxBytes)
                {
                    throw new ProtocolException(string.Format(
                        "single entity record {0} bytes exceeds slice budget {1}", recordBytes, maxBytes));
                }
                if (current.Count > 0 && currentBytes + recordBytes > maxBytes)
                {
                    parts.Add(current.ToArray());
                    current = new List<EntitySnapshot>();
                    currentBytes = 0;
                }
                current.Add(snap);
                currentBytes += recordBytes;
            }

            parts.Add(current.ToArray());
            return parts;
        }

        /// <summary>On-wire size of one entity record inside a StateSnapshot:
        /// uint16 length prefix + encoded EntitySnapshot bytes.</summary>
        public static int RecordBytes(EntitySnapshot snap)
        {
            return 2 + EntitySnapshotCodec.Encode(snap).Length;
        }
    }
}
