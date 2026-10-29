using System;

namespace IceyCoop.Core
{
    /// <summary>What the room should do about the current downed state.</summary>
    public enum WipeDecision
    {
        None = 0,
        /// <summary>One player is down; the other must survive. No reload yet.</summary>
        AwaitPartner = 1,
        /// <summary>Both down: the host restores the last checkpoint.</summary>
        RestoreCheckpoint = 2,
        /// <summary>Restores kept failing; the session must end honestly.</summary>
        EndRoom = 3
    }

    /// <summary>
    /// Party wipe / checkpoint policy (plan T20).
    ///
    /// Pure logic with an injected clock: the GameBridge feeds real downed edges
    /// and ticks in, and gets a decision back. Keeping the policy here means the
    /// "who dies, what happens, how many retries" rules can be unit tested instead
    /// of discovered in a live boss fight.
    ///
    /// Rules:
    ///  - one player down: wait, never reload (the partner can still finish);
    ///  - both down: wait GraceMs, then restore the checkpoint;
    ///  - a restore that fails EndRoomAfterFailures times ends the room instead of
    ///    leaving a silently broken session;
    ///  - a revive clears that player's flag, and any successful restore clears
    ///    both.
    /// </summary>
    public sealed class PartyWipeCoordinator
    {
        public const uint DefaultGraceMs = 1200;
        public const int DefaultMaxRestoreFailures = 2;

        private readonly uint graceMs;
        private readonly int maxRestoreFailures;

        private bool p1Downed;
        private bool p2Downed;
        private uint p1DownedAt;
        private uint p2DownedAt;
        private uint wipeStartedAt;
        private bool wipePending;
        private int restoreFailures;

        public PartyWipeCoordinator()
            : this(DefaultGraceMs, DefaultMaxRestoreFailures)
        {
        }

        public PartyWipeCoordinator(uint graceWindowMs, int maxFailures)
        {
            graceMs = graceWindowMs;
            maxRestoreFailures = maxFailures <= 0 ? 1 : maxFailures;
        }

        public bool P1Downed
        {
            get { return p1Downed; }
        }

        public bool P2Downed
        {
            get { return p2Downed; }
        }

        public bool IsWipe
        {
            get { return p1Downed && p2Downed; }
        }

        public int RestoreFailures
        {
            get { return restoreFailures; }
        }

        public void ReportDowned(byte playerId, uint nowMs)
        {
            if (playerId == PlayerIds.P1)
            {
                if (!p1Downed)
                {
                    p1DownedAt = nowMs;
                }
                p1Downed = true;
            }
            else if (playerId == PlayerIds.P2)
            {
                if (!p2Downed)
                {
                    p2DownedAt = nowMs;
                }
                p2Downed = true;
            }
            else
            {
                return;
            }
            if (p1Downed && p2Downed && !wipePending)
            {
                wipePending = true;
                wipeStartedAt = nowMs;
            }
        }

        public void ReportRevived(byte playerId)
        {
            if (playerId == PlayerIds.P1)
            {
                p1Downed = false;
            }
            else if (playerId == PlayerIds.P2)
            {
                p2Downed = false;
            }
            if (!(p1Downed && p2Downed))
            {
                wipePending = false;
                restoreFailures = 0;
            }
        }

        /// <summary>Called after a restore attempt. Success clears the wipe.</summary>
        public void ReportRestoreResult(bool succeeded)
        {
            if (succeeded)
            {
                p1Downed = false;
                p2Downed = false;
                wipePending = false;
                restoreFailures = 0;
                return;
            }
            restoreFailures++;
        }

        /// <summary>
        /// Current decision for this tick. RestoreCheckpoint is returned exactly
        /// once per grace window: the caller performs the restore and reports back.
        /// </summary>
        public WipeDecision Poll(uint nowMs)
        {
            if (!(p1Downed && p2Downed))
            {
                if (p1Downed || p2Downed)
                {
                    return WipeDecision.AwaitPartner;
                }
                return WipeDecision.None;
            }
            if (restoreFailures >= maxRestoreFailures)
            {
                return WipeDecision.EndRoom;
            }
            if (!wipePending)
            {
                return WipeDecision.None;
            }
            if (nowMs - wipeStartedAt < graceMs)
            {
                return WipeDecision.AwaitPartner;
            }
            // Consume the window so the caller is not asked twice.
            wipePending = false;
            return WipeDecision.RestoreCheckpoint;
        }

        public void Reset()
        {
            p1Downed = false;
            p2Downed = false;
            p1DownedAt = 0;
            p2DownedAt = 0;
            wipePending = false;
            restoreFailures = 0;
        }

        /// <summary>Milliseconds the P1 corpse has been down (diagnostics).</summary>
        public uint P1DownedFor(uint nowMs)
        {
            return p1Downed && nowMs >= p1DownedAt ? nowMs - p1DownedAt : 0;
        }

        /// <summary>Milliseconds the P2 corpse has been down (diagnostics).</summary>
        public uint P2DownedFor(uint nowMs)
        {
            return p2Downed && nowMs >= p2DownedAt ? nowMs - p2DownedAt : 0;
        }
    }
}
