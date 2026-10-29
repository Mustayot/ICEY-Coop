using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>One registered native entity (enemy / player / encounter token).</summary>
    public struct EntityRecord
    {
        public ulong EntityId;
        public uint WorldEpoch;
        public int Kind;
        public string SourceKey;
        public uint Generation;
        public ulong Revision;
        public bool Despawned;
    }

    /// <summary>
    /// Process-local stable entity ids. Ids are allocated monotonically and are
    /// never reused; despawn leaves a tombstone. Object-level idempotence (a
    /// re-enabled object must not get a second id) is enforced by the GameBridge
    /// EntityAdapter via a reference-equality object -> id cache.
    /// </summary>
    public sealed class EntityRegistry
    {
        private readonly Dictionary<ulong, EntityRecord> records = new Dictionary<ulong, EntityRecord>();
        private readonly Dictionary<string, uint> generations = new Dictionary<string, uint>();
        private ulong nextId;

        public EntityRegistry() : this(0) { }

        /// <summary>Keep externally assigned actor ids outside the dynamic range.</summary>
        public EntityRegistry(ulong lastReservedId)
        {
            nextId = lastReservedId;
        }

        public int Count
        {
            get { return records.Count; }
        }

        public ulong Register(uint worldEpoch, int kind, string sourceKey, uint generation, out bool isNew)
        {
            if (nextId == ulong.MaxValue)
            {
                throw new InvalidOperationException("entity id overflow");
            }
            nextId++;
            ulong id = nextId;

            EntityRecord record = new EntityRecord();
            record.EntityId = id;
            record.WorldEpoch = worldEpoch;
            record.Kind = kind;
            record.SourceKey = sourceKey == null ? string.Empty : sourceKey;
            record.Generation = generation;
            record.Revision = 0;
            record.Despawned = false;
            records[id] = record;
            isNew = true;
            return id;
        }

        public bool TryGet(ulong entityId, out EntityRecord record)
        {
            return records.TryGetValue(entityId, out record);
        }

        /// <summary>Marks an entity despawned with the given revision. Idempotent.</summary>
        public bool MarkDespawn(ulong entityId, ulong revision)
        {
            EntityRecord record;
            if (!records.TryGetValue(entityId, out record))
            {
                return false;
            }
            record.Despawned = true;
            record.Revision = revision;
            records[entityId] = record;
            return true;
        }

        /// <summary>Advances and returns the generation for an encounter key.</summary>
        public uint BumpGeneration(string encounterKey)
        {
            if (string.IsNullOrEmpty(encounterKey))
            {
                throw new ArgumentException("encounterKey");
            }
            uint next;
            if (!generations.TryGetValue(encounterKey, out next))
            {
                next = 1;
            }
            else
            {
                if (next == uint.MaxValue)
                {
                    throw new InvalidOperationException("generation overflow");
                }
                next = next + 1;
            }
            generations[encounterKey] = next;
            return next;
        }
    }
}
