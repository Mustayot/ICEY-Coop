using System;

namespace IceyCoop.Protocol
{
    /// <summary>
    /// Bounds and orders the unreliable StateSnapshot stream on the client.
    /// A StateSnapshot contains only the entities assigned to its one part, so
    /// parts with the same SnapshotId must remain independently admissible.
    /// Entity revision/tombstone merging remains ReplicaStore's responsibility.
    /// </summary>
    public sealed class SnapshotFreshnessGate
    {
        // The host caps the snapshot at 4096 entities and each UDP part carries
        // at least one entity (except the single empty-world part). This bound
        // keeps malformed PartCount values from allocating unbounded memory.
        private const int MaxTrackedParts = MessageConstants.MaxSnapshotEntities;

        private bool hasBaseline;
        private uint epoch;
        private ulong baselineHostTick;
        private ulong baselineIncludedEventSeq;
        private bool hasSnapshot;
        private ulong latestSnapshotId;
        private ulong latestHostTick;
        private ulong latestIncludedEventSeq;
        private ushort latestPartCount;
        private bool[] receivedParts;

        public uint Epoch { get { return epoch; } }
        public ulong LatestSnapshotId { get { return hasSnapshot ? latestSnapshotId : 0; } }

        /// <summary>Clears state when a client session/link is replaced.</summary>
        public void Reset()
        {
            hasBaseline = false;
            epoch = 0;
            baselineHostTick = 0;
            baselineIncludedEventSeq = 0;
            ClearSnapshotState();
        }

        /// <summary>
        /// Arms the snapshot stream after a baseline is accepted. HostTick and
        /// IncludedEventSeq come from the matching BaselineBegin; older queued
        /// UDP packets are rejected without changing the wire schema. StateSnapshot
        /// carries no epoch/session id, so these floors cannot distinguish an old
        /// process packet whose tick and event cursor happen to exceed a new
        /// process baseline's values.
        /// </summary>
        public void BeginBaseline(uint baselineEpoch, ulong hostTick, ulong includedEventSeq)
        {
            hasBaseline = true;
            epoch = baselineEpoch;
            baselineHostTick = hostTick;
            baselineIncludedEventSeq = includedEventSeq;
            ClearSnapshotState();
        }

        /// <summary>
        /// Admits one independently applicable packet. Multiple distinct parts
        /// from the newest SnapshotId pass; duplicate parts and packets from an
        /// older snapshot do not. IncludedEventSeq is a floor, not an exact
        /// equality check: snapshots are partial state and may be sampled after
        /// the baseline has accumulated more reliable events.
        /// </summary>
        public bool TryAcceptPart(StateSnapshotMessage snapshot)
        {
            if (!hasBaseline || snapshot == null || snapshot.SnapshotId == 0 ||
                snapshot.PartCount == 0 || snapshot.PartIndex >= snapshot.PartCount ||
                snapshot.PartCount > MaxTrackedParts)
            {
                return false;
            }
            if (snapshot.HostTick < baselineHostTick ||
                snapshot.IncludedEventSeq < baselineIncludedEventSeq)
            {
                return false;
            }

            if (!hasSnapshot || snapshot.SnapshotId > latestSnapshotId)
            {
                if (hasSnapshot && (snapshot.HostTick < latestHostTick ||
                    snapshot.IncludedEventSeq < latestIncludedEventSeq))
                {
                    return false;
                }
                hasSnapshot = true;
                latestSnapshotId = snapshot.SnapshotId;
                latestHostTick = snapshot.HostTick;
                latestIncludedEventSeq = snapshot.IncludedEventSeq;
                latestPartCount = snapshot.PartCount;
                receivedParts = new bool[snapshot.PartCount];
            }
            else if (snapshot.SnapshotId < latestSnapshotId)
            {
                return false;
            }
            else if (snapshot.HostTick != latestHostTick ||
                snapshot.IncludedEventSeq != latestIncludedEventSeq ||
                snapshot.PartCount != latestPartCount)
            {
                // All parts of one assembled snapshot share these fields. A
                // conflicting packet must not reset or extend the seen bitmap.
                return false;
            }

            if (receivedParts[snapshot.PartIndex])
            {
                return false;
            }
            receivedParts[snapshot.PartIndex] = true;
            return true;
        }

        private void ClearSnapshotState()
        {
            hasSnapshot = false;
            latestSnapshotId = 0;
            latestHostTick = 0;
            latestIncludedEventSeq = 0;
            latestPartCount = 0;
            receivedParts = null;
        }
    }
}
