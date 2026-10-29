using System;

namespace IceyCoop.Core
{
    /// <summary>Where a partner-revive transaction stands.</summary>
    public enum ReviveState
    {
        /// <summary>Nothing is being revived.</summary>
        Idle = 0,

        /// <summary>A revive was requested; waiting for the target's world (scene, roots, binding).</summary>
        AwaitingWorld = 1,

        /// <summary>The one-shot native revive sequence is being executed by the caller.</summary>
        Committing = 2,

        /// <summary>The native sequence ran; waiting for the completion conditions to hold.</summary>
        Confirming = 3,

        /// <summary>The partner is back: every completion condition held.</summary>
        Done = 4,

        /// <summary>The transaction gave up after it may have touched native state; a rollback is owed.</summary>
        Failed = 5,

        /// <summary>Dropped before anything was written; nothing to roll back.</summary>
        Aborted = 6
    }

    /// <summary>Why a revive request was refused.</summary>
    public enum ReviveRefusal
    {
        /// <summary>The request was accepted.</summary>
        None = 0,

        /// <summary>Another revive is already in flight; revives never queue.</summary>
        Busy = 1,

        /// <summary>The player id is not a co-op player id.</summary>
        InvalidPlayer = 2,

        /// <summary>Both players are down: that is a team wipe and belongs to the wipe coordinator.</summary>
        TeamWipe = 3,

        /// <summary>No reason was supplied, so the transaction could not be audited.</summary>
        MissingReason = 4
    }

    /// <summary>
    /// T20 partner revive (see docs/G5-T20-队友恢复设计-2026-10-23.md).
    ///
    /// The gap: vanilla only ever brings a player back through <c>LevelManager.LoadGame</c>
    /// (a whole-world reload). A single downed player therefore stays a corpse until the
    /// partner also dies. This gate owns the *decision* side of pulling that player back up
    /// when the team reaches a shared checkpoint or both cross a scene together - a pure
    /// state machine so the timing rules can be unit tested instead of discovered in a fight.
    ///
    /// Deliberate rules (each one is a test):
    ///  - nothing is written before commit: every readiness check happens first;
    ///  - the native sequence is issued exactly once (a second pass would silently refill HP);
    ///  - Done/Failed/Aborted are terminal: a late world-ready report never revives the
    ///    transaction - only a fresh Request does;
    ///  - Abort is only legal before commit; after commit the caller owes a rollback, which is
    ///    reported as Failed;
    ///  - one target at a time, no queueing;
    ///  - a team wipe is never handled here.
    ///
    /// Pure logic: no Unity, no clock of its own - milliseconds come from the caller.
    /// </summary>
    public sealed class ReviveGate
    {
        public const int DefaultRetryMs = 250;
        public const int DefaultDeadlineMs = 3000;
        public const int DefaultConfirmMs = 1000;

        private readonly int retryMs;
        private readonly int deadlineMs;
        private readonly int confirmMs;

        private long armedAtMs;
        private long confirmUntilMs;

        public ReviveGate() : this(DefaultRetryMs, DefaultDeadlineMs, DefaultConfirmMs)
        {
        }

        public ReviveGate(int retryMs, int deadlineMs, int confirmMs)
        {
            if (retryMs < 0)
            {
                throw new ArgumentOutOfRangeException("retryMs", retryMs, "retry interval cannot be negative");
            }
            if (deadlineMs <= 0)
            {
                throw new ArgumentOutOfRangeException("deadlineMs", deadlineMs, "deadline must be positive");
            }
            if (confirmMs <= 0)
            {
                throw new ArgumentOutOfRangeException("confirmMs", confirmMs, "confirm window must be positive");
            }
            this.retryMs = retryMs;
            this.deadlineMs = deadlineMs;
            this.confirmMs = confirmMs;
            State = ReviveState.Idle;
            Reason = string.Empty;
            LastError = string.Empty;
        }

        public int RetryMs { get { return retryMs; } }

        public int DeadlineMs { get { return deadlineMs; } }

        public int ConfirmMs { get { return confirmMs; } }

        public ReviveState State { get; private set; }

        /// <summary>The player being revived; 0 when idle.</summary>
        public byte Target { get; private set; }

        /// <summary>Why the transaction was started (checkpoint / scene crossing / diagnostics).</summary>
        public string Reason { get; private set; }

        /// <summary>Why the last world check or completion check did not pass.</summary>
        public string LastError { get; private set; }

        /// <summary>Set once when the transaction reaches Failed.</summary>
        public string FailureReason { get; private set; }

        /// <summary>How many times the native revive sequence was issued (0 or 1 per transaction).</summary>
        public int Commits { get; private set; }

        /// <summary>How many world-not-ready reports were observed (diagnostics).</summary>
        public int WorldChecks { get; private set; }

        /// <summary>How many confirm observations were made (diagnostics).</summary>
        public int ConfirmChecks { get; private set; }

        public bool IsActive
        {
            get
            {
                return State == ReviveState.AwaitingWorld
                    || State == ReviveState.Committing
                    || State == ReviveState.Confirming;
            }
        }

        public bool IsTerminal
        {
            get
            {
                return State == ReviveState.Done
                    || State == ReviveState.Failed
                    || State == ReviveState.Aborted;
            }
        }

        /// <summary>
        /// Asks to bring <paramref name="playerId"/> back. A team wipe must not come here:
        /// two downed players are the wipe coordinator's business.
        /// </summary>
        public ReviveRefusal Request(byte playerId, bool teamWipe, string reason, long nowMs)
        {
            if (playerId != PlayerIds.P1 && playerId != PlayerIds.P2)
            {
                return ReviveRefusal.InvalidPlayer;
            }
            if (IsActive)
            {
                return ReviveRefusal.Busy;
            }
            if (teamWipe)
            {
                return ReviveRefusal.TeamWipe;
            }
            if (reason == null || reason.Length == 0)
            {
                return ReviveRefusal.MissingReason;
            }

            Target = playerId;
            Reason = reason;
            LastError = string.Empty;
            FailureReason = string.Empty;
            Commits = 0;
            WorldChecks = 0;
            ConfirmChecks = 0;
            armedAtMs = nowMs;
            confirmUntilMs = 0L;
            State = ReviveState.AwaitingWorld;
            return ReviveRefusal.None;
        }

        /// <summary>
        /// Reports whether the target's world is ready. Returns true when the caller must now
        /// execute the native revive sequence exactly once. Nothing has been written until then.
        /// </summary>
        public bool ObserveWorld(bool ready, string reason, long nowMs)
        {
            if (State != ReviveState.AwaitingWorld)
            {
                // Terminal and later states never restart: a late ready report is ignored.
                return false;
            }
            if (nowMs - armedAtMs >= deadlineMs)
            {
                LastError = ready ? string.Empty : (reason == null ? "world not ready" : reason);
                Fail("world not ready before the revive deadline: " + LastError);
                return false;
            }
            if (!ready)
            {
                WorldChecks++;
                LastError = reason == null ? "world not ready" : reason;
                return false;
            }
            Commits++;
            State = ReviveState.Committing;
            return true;
        }

        /// <summary>Reports the outcome of the one-shot native revive sequence.</summary>
        public void NoteCommitted(bool executed, string error, long nowMs)
        {
            if (State != ReviveState.Committing)
            {
                return;
            }
            if (!executed)
            {
                Fail("native revive sequence failed: " + (error == null ? "unknown error" : error));
                return;
            }
            LastError = string.Empty;
            confirmUntilMs = nowMs + confirmMs;
            State = ReviveState.Confirming;
        }

        /// <summary>
        /// Reports the completion-condition check. Returns true once the transaction is Done.
        /// When the confirm window closes without every condition holding, the transaction is
        /// Failed - a half-revived player is never reported as a success.
        /// </summary>
        public bool ObserveConfirm(bool satisfied, string error, long nowMs)
        {
            if (State == ReviveState.Done)
            {
                return true;
            }
            if (State != ReviveState.Confirming)
            {
                return false;
            }
            ConfirmChecks++;
            if (satisfied)
            {
                LastError = string.Empty;
                State = ReviveState.Done;
                return true;
            }
            LastError = error == null ? "completion conditions not met" : error;
            if (nowMs >= confirmUntilMs)
            {
                Fail("revive not confirmed within the confirm window: " + LastError);
            }
            return false;
        }

        /// <summary>
        /// Drops the transaction before anything was written. Only legal while still waiting for
        /// the world; once committed the caller owes a rollback and must report Failed instead.
        /// </summary>
        public bool Abort(string reason)
        {
            if (State != ReviveState.AwaitingWorld)
            {
                return false;
            }
            LastError = reason == null ? string.Empty : reason;
            State = ReviveState.Aborted;
            return true;
        }

        /// <summary>Back to Idle at a session boundary.</summary>
        public void Reset()
        {
            State = ReviveState.Idle;
            Target = 0;
            Reason = string.Empty;
            LastError = string.Empty;
            FailureReason = string.Empty;
            Commits = 0;
            WorldChecks = 0;
            ConfirmChecks = 0;
            armedAtMs = 0L;
            confirmUntilMs = 0L;
        }

        /// <summary>
        /// Invariant I1 from the design: HP and the death flag must never disagree. A revive that
        /// leaves HP&gt;0 with DeadFlag still set (or the reverse) is the exact "claimed a
        /// resurrection without completing it" failure the plan forbids.
        /// </summary>
        public static bool IsConsistent(int hp, bool deadFlag)
        {
            return (hp > 0) == !deadFlag;
        }

        /// <summary>Convenience for the caller's log line.</summary>
        public static string NameOf(ReviveState state)
        {
            switch (state)
            {
                case ReviveState.Idle: return "Idle";
                case ReviveState.AwaitingWorld: return "AwaitingWorld";
                case ReviveState.Committing: return "Committing";
                case ReviveState.Confirming: return "Confirming";
                case ReviveState.Done: return "Done";
                case ReviveState.Failed: return "Failed";
                case ReviveState.Aborted: return "Aborted";
                default: return state.ToString();
            }
        }

        private void Fail(string reason)
        {
            State = ReviveState.Failed;
            FailureReason = reason;
            if (string.IsNullOrEmpty(LastError))
            {
                LastError = reason;
            }
        }
    }
}
