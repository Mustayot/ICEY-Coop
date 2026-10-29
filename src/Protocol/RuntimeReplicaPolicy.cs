namespace IceyCoop.Protocol
{
    /// <summary>
    /// Runtime ordering policy for the two independently transported replica
    /// streams. This does not change ReplicaStore's lower-level snapshot
    /// contract; it prevents the GameBridge from presenting a UDP sample until
    /// its reliable WorldEvent cursor has caught up to that sample.
    /// </summary>
    public static class RuntimeReplicaPolicy
    {
        /// <summary>Only present a snapshot sampled through the applied event cursor.</summary>
        public static bool CanPresentSnapshot(ReplicaStore store, StateSnapshotMessage snapshot)
        {
            return store != null && snapshot != null && snapshot.IncludedEventSeq == store.AppliedEventSeq;
        }

        /// <summary>
        /// Builds the entity record used to advance the reliable event cursor.
        /// Entity-bearing snapshot payloads keep their transmitted kind/revision;
        /// events without such a payload preserve the known entity kind and never
        /// lower its revision. Target zero remains the revisionless event sentinel.
        /// </summary>
        public static EntityRecord CreateEventRecord(ReplicaStore store, WorldEventMessage ev,
            EntitySnapshot snapshot, bool snapshotPayload)
        {
            if (ev == null)
            {
                return new EntityRecord();
            }

            bool despawned = ev.SubType == (byte)WorldEventSubType.EntityDespawn;
            if (ev.TargetEntityId == 0)
            {
                return new EntityRecord(0, 0, snapshot.Kind, false);
            }

            if (snapshotPayload)
            {
                return new EntityRecord(ev.TargetEntityId, ev.EntityRevision, snapshot.Kind, despawned);
            }

            EntityRecord existing;
            if (store != null && store.TryGetEntity(ev.TargetEntityId, out existing))
            {
                ulong revision = existing.Revision > ev.EntityRevision ? existing.Revision : ev.EntityRevision;
                return new EntityRecord(ev.TargetEntityId, revision, existing.Kind, false);
            }

            // Preserve the old unknown-kind fallback while retaining revision
            // information from entity-addressed reliable events (e.g. HitResolved).
            return new EntityRecord(ev.TargetEntityId, ev.EntityRevision, snapshot.Kind, false);
        }
    }
}
