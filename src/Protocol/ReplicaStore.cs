using System;
using System.Collections.Generic;

namespace IceyCoop.Protocol
{
    public enum ApplyResult
    {
        Applied = 0,
        StaleEvent = 1,
        GapEvent = 2,
        RevisionRollback = 3,
        WrongEpoch = 4,
        UnknownEntity = 5,
        TombstoneConflict = 6
    }

    public struct EntityRecord
    {
        public ulong EntityId;
        public ulong Revision;
        public int Kind;
        public bool Despawned;

        public EntityRecord(ulong entityId, ulong revision, int kind, bool despawned)
        {
            EntityId = entityId;
            Revision = revision;
            Kind = kind;
            Despawned = despawned;
        }
    }

    /// <summary>
    /// Client-side replica: applies a baseline, then strictly ordered events and
    /// idempotent snapshots. Enforces per-entity revision monotonicity and
    /// despawn tombstones (a dead entity cannot be revived by an old snapshot).
    /// Snapshots merge entity state only and never advance appliedEventSeq; that
    /// cursor is advanced solely by ApplyEvent consuming reliable events in order.
    ///
    /// Scope decision (this round): "unknown entity waits for an explicit Spawn"
    /// is NOT tightened. EntityRecord.Kind does not currently distinguish a spawn
    /// from a state update, so every non-despawn record is treated as a
    /// spawn/upsert. Changing that semantics is out of scope for this round.
    /// TODO: once WorldEvent carries an explicit spawn marker, tighten the
    /// unknown-entity path accordingly.
    /// </summary>
    public sealed class ReplicaStore
    {
        private readonly Dictionary<ulong, EntityRecord> entities = new Dictionary<ulong, EntityRecord>();

        private uint epoch;
        private ulong includedEventSeq;
        private ulong appliedEventSeq;

        public uint Epoch
        {
            get { return epoch; }
        }

        public ulong AppliedEventSeq
        {
            get { return appliedEventSeq; }
        }

        public int EntityCount
        {
            get { return entities.Count; }
        }

        public ApplyResult ApplyBaseline(uint baselineEpoch, ulong baselineIncludedEventSeq, EntityRecord[] records)
        {
            if (baselineEpoch < epoch)
            {
                return ApplyResult.WrongEpoch;
            }
            epoch = baselineEpoch;
            includedEventSeq = baselineIncludedEventSeq;
            appliedEventSeq = baselineIncludedEventSeq;
            entities.Clear();
            if (records == null)
            {
                return ApplyResult.Applied;
            }
            foreach (EntityRecord record in records)
            {
                entities[record.EntityId] = record;
            }
            return ApplyResult.Applied;
        }

        public ApplyResult ApplyEvent(uint eventEpoch, ulong eventSeq, EntityRecord record)
        {
            if (eventEpoch != epoch)
            {
                return ApplyResult.WrongEpoch;
            }
            if (eventSeq <= appliedEventSeq)
            {
                return ApplyResult.StaleEvent;
            }
            if (eventSeq != appliedEventSeq + 1)
            {
                return ApplyResult.GapEvent;
            }

            ApplyResult result = MergeRecord(record);
            if (result == ApplyResult.Applied)
            {
                appliedEventSeq = eventSeq;
            }
            return result;
        }

        public ApplyResult ApplySnapshot(uint snapshotEpoch, ulong snapshotIncludedEventSeq, EntityRecord[] records)
        {
            if (snapshotEpoch != epoch)
            {
                return ApplyResult.WrongEpoch;
            }
            if (snapshotIncludedEventSeq < appliedEventSeq)
            {
                return ApplyResult.StaleEvent;
            }

            if (records == null)
            {
                return ApplyResult.Applied;
            }
            foreach (EntityRecord record in records)
            {
                ApplyResult result = MergeRecord(record);
                if (result != ApplyResult.Applied)
                {
                    return result;
                }
            }
            return ApplyResult.Applied;
        }

        public bool TryGetEntity(ulong entityId, out EntityRecord record)
        {
            return entities.TryGetValue(entityId, out record);
        }

        private ApplyResult MergeRecord(EntityRecord record)
        {
            EntityRecord existing;
            bool hasExisting = entities.TryGetValue(record.EntityId, out existing);

            if (record.Despawned)
            {
                if (!hasExisting)
                {
                    return ApplyResult.UnknownEntity;
                }
                // Revision check before writing the tombstone: Despawn(rev20)
                // must not tombstone an entity already at rev21.
                if (record.Revision < existing.Revision)
                {
                    return ApplyResult.RevisionRollback;
                }
                existing.Despawned = true;
                existing.Revision = record.Revision;
                entities[record.EntityId] = existing;
                return ApplyResult.Applied;
            }

            if (hasExisting && existing.Despawned)
            {
                return ApplyResult.TombstoneConflict;
            }
            if (hasExisting && record.Revision < existing.Revision)
            {
                return ApplyResult.RevisionRollback;
            }

            entities[record.EntityId] = record;
            return ApplyResult.Applied;
        }
    }
}
