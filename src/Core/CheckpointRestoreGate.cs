using System;

namespace IceyCoop.Core
{
    /// <summary>Where a team-checkpoint restore stands.</summary>
    public enum CheckpointRestoreState
    {
        /// <summary>Nothing asked for a restore.</summary>
        Idle = 0,

        /// <summary>A wipe armed a restore, but no cooper save has been captured yet.</summary>
        WaitingForSnapshot = 1,

        /// <summary>A snapshot exists; the bridge keeps trying until it applies.</summary>
        Ready = 2,

        /// <summary>The checkpoint was applied to the live world.</summary>
        Done = 3,

        /// <summary>The deadline passed without a clean apply; the session is told to stop trying.</summary>
        Failed = 4
    }

    /// <summary>
    /// T20: after a team wipe the native reload puts the players back at the *vanilla* save,
    /// which is not the co-op checkpoint (the co-op save path never lets the game touch
    /// save_data.bin). So the team checkpoint has to be re-applied by us, and that apply can
    /// legitimately fail for a while - the reload is still running, the actors are still dead,
    /// the scene has not switched yet. This gate owns that retry loop so it is not spread across
    /// the bridge, and so the "give up" condition is explicit instead of accidental.
    ///
    /// Pure logic: no Unity, no clock of its own - the caller passes milliseconds.
    /// </summary>
    public sealed class CheckpointRestoreGate
    {
        private readonly float retryIntervalMs;
        private readonly float deadlineMs;

        private bool armed;
        private long armedAtMs;
        private long nextAttemptAtMs;

        public CheckpointRestoreGate() : this(500f, 20000f)
        {
        }

        public CheckpointRestoreGate(float retryIntervalMs, float deadlineMs)
        {
            if (retryIntervalMs < 0f)
            {
                throw new ArgumentOutOfRangeException("retryIntervalMs", retryIntervalMs, "retry interval cannot be negative");
            }
            if (deadlineMs <= 0f)
            {
                throw new ArgumentOutOfRangeException("deadlineMs", deadlineMs, "deadline must be positive");
            }
            this.retryIntervalMs = retryIntervalMs;
            this.deadlineMs = deadlineMs;
            State = CheckpointRestoreState.Idle;
        }

        public float RetryIntervalMs { get { return retryIntervalMs; } }

        public float DeadlineMs { get { return deadlineMs; } }

        public CheckpointRestoreState State { get; private set; }

        /// <summary>How many apply attempts were made (diagnostics).</summary>
        public int Attempts { get; private set; }

        /// <summary>Why the last attempt did not apply, or why the gate gave up.</summary>
        public string LastError { get; private set; }

        public bool Armed { get { return armed; } }

        /// <summary>A wipe happened: start waiting for a cooper snapshot to re-apply.</summary>
        public void Arm(long nowMs)
        {
            armed = true;
            armedAtMs = nowMs;
            nextAttemptAtMs = nowMs;
            Attempts = 0;
            LastError = string.Empty;
            State = CheckpointRestoreState.WaitingForSnapshot;
        }

        /// <summary>Tell the gate whether a cooper save could be read.</summary>
        public void NoteSnapshot(bool available, long nowMs)
        {
            NoteSnapshot(available, nowMs, null);
        }

        /// <summary>Tell the gate whether a co-op save could be read and why it was unavailable.</summary>
        public void NoteSnapshot(bool available, long nowMs, string unavailableReason)
        {
            if (!armed)
            {
                return;
            }

            if (IsPastDeadline(nowMs))
            {
                string reason;
                if (available && State == CheckpointRestoreState.WaitingForSnapshot)
                {
                    reason = "checkpoint snapshot arrived after the restore deadline";
                }
                else if (!available)
                {
                    reason = string.IsNullOrEmpty(unavailableReason)
                        ? "no readable co-op checkpoint snapshot"
                        : unavailableReason;
                }
                else
                {
                    reason = string.IsNullOrEmpty(LastError)
                        ? "checkpoint restore was not applied before the deadline"
                        : "last apply failed: " + LastError;
                }
                FailDeadline(reason);
                return;
            }

            if (available && State == CheckpointRestoreState.WaitingForSnapshot)
            {
                State = CheckpointRestoreState.Ready;
                nextAttemptAtMs = nowMs;
                LastError = string.Empty;
                return;
            }
            if (!available && State == CheckpointRestoreState.Ready)
            {
                // The file vanished (deleted / half written): go back to waiting rather than
                // keeping a snapshot we cannot read.
                State = CheckpointRestoreState.WaitingForSnapshot;
                LastError = string.IsNullOrEmpty(unavailableReason)
                    ? "co-op checkpoint snapshot is no longer readable"
                    : unavailableReason;
                return;
            }
            if (!available && State == CheckpointRestoreState.WaitingForSnapshot)
            {
                LastError = string.IsNullOrEmpty(unavailableReason)
                    ? "no readable co-op checkpoint snapshot"
                    : unavailableReason;
            }
        }

        /// <summary>True when the caller should try the apply now.</summary>
        public bool ShouldAttempt(long nowMs)
        {
            if (!armed || State != CheckpointRestoreState.Ready)
            {
                return false;
            }
            if (IsPastDeadline(nowMs))
            {
                FailDeadline(string.IsNullOrEmpty(LastError)
                    ? "checkpoint restore was not applied before the deadline"
                    : "last apply failed: " + LastError);
                return false;
            }
            return nowMs >= nextAttemptAtMs;
        }

        /// <summary>Report the outcome of one apply attempt.</summary>
        public void NoteAttempt(bool applied, string error, long nowMs)
        {
            if (!armed || State != CheckpointRestoreState.Ready)
            {
                return;
            }
            if (IsPastDeadline(nowMs))
            {
                string reason = !applied && !string.IsNullOrEmpty(error)
                    ? "last apply failed: " + error
                    : applied
                        ? "checkpoint apply completed after the restore deadline"
                        : string.IsNullOrEmpty(LastError)
                            ? "checkpoint restore was not applied before the deadline"
                            : "last apply failed: " + LastError;
                FailDeadline(reason);
                return;
            }
            Attempts++;
            if (applied)
            {
                State = CheckpointRestoreState.Done;
                armed = false;
                LastError = string.Empty;
                return;
            }
            LastError = error ?? string.Empty;
            nextAttemptAtMs = nowMs + (long)retryIntervalMs;
        }

        private bool IsPastDeadline(long nowMs)
        {
            return nowMs - armedAtMs >= deadlineMs;
        }

        private void FailDeadline(string reason)
        {
            LastError = "restore deadline expired: " + reason;
            State = CheckpointRestoreState.Failed;
            armed = false;
        }

        /// <summary>Back to Idle (a new session, or the room ended).</summary>
        public void Reset()
        {
            armed = false;
            armedAtMs = 0L;
            nextAttemptAtMs = 0L;
            Attempts = 0;
            LastError = string.Empty;
            State = CheckpointRestoreState.Idle;
        }
    }
}
