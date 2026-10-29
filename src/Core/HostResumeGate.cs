using System;

namespace IceyCoop.Core
{
    /// <summary>The host startup checkpoint restore lifecycle.</summary>
    public enum HostResumeState
    {
        Idle = 0,
        Pending = 1,
        Done = 2,
        Failed = 3
    }

    /// <summary>
    /// Controls the optional host startup restore before normal networking and save writes begin.
    /// Pure logic: the caller supplies monotonic milliseconds and performs each apply attempt.
    /// </summary>
    public sealed class HostResumeGate
    {
        private readonly int retryMs;
        private readonly int deadlineMs;
        private decimal deadlineAtMs;
        private decimal nextAttemptAtMs;

        public HostResumeGate() : this(500, 60000)
        {
        }

        public HostResumeGate(int retryMs, int deadlineMs)
        {
            if (retryMs < 0)
            {
                throw new ArgumentOutOfRangeException("retryMs", retryMs, "retry interval cannot be negative");
            }
            if (deadlineMs <= 0)
            {
                throw new ArgumentOutOfRangeException("deadlineMs", deadlineMs, "deadline must be positive");
            }

            this.retryMs = retryMs;
            this.deadlineMs = deadlineMs;
            State = HostResumeState.Idle;
            LastError = string.Empty;
        }

        public HostResumeState State { get; private set; }

        public int Attempts { get; private set; }

        public string LastError { get; private set; }

        public bool CanListen
        {
            get { return State == HostResumeState.Idle || State == HostResumeState.Done; }
        }

        public bool CanWriteSave
        {
            get { return State == HostResumeState.Idle || State == HostResumeState.Done; }
        }

        /// <summary>Begin one optional startup restore. A new session requires Reset first.</summary>
        public void Begin(bool requested, long nowMs)
        {
            if (State != HostResumeState.Idle || !requested)
            {
                return;
            }

            State = HostResumeState.Pending;
            Attempts = 0;
            LastError = string.Empty;
            deadlineAtMs = (decimal)nowMs + deadlineMs;
            nextAttemptAtMs = nowMs;
        }

        /// <summary>True when the caller may perform an apply attempt now.</summary>
        public bool ShouldAttempt(long nowMs)
        {
            if (State != HostResumeState.Pending)
            {
                return false;
            }
            if ((decimal)nowMs >= deadlineAtMs)
            {
                FailDeadline(string.Empty);
                return false;
            }
            return (decimal)nowMs >= nextAttemptAtMs;
        }

        /// <summary>Report the result of an apply attempt made by the caller.</summary>
        public void NoteAttempt(bool applied, string error, long nowMs)
        {
            if (State != HostResumeState.Pending)
            {
                return;
            }
            if ((decimal)nowMs >= deadlineAtMs)
            {
                FailDeadline(applied ? "apply completed after deadline" : error);
                return;
            }

            Attempts++;
            if (applied)
            {
                State = HostResumeState.Done;
                LastError = string.Empty;
                return;
            }

            LastError = error ?? string.Empty;
            nextAttemptAtMs = (decimal)nowMs + retryMs;
        }

        /// <summary>Terminally fail a pending restore, for example when its snapshot is invalid.</summary>
        public void Fail(string reason)
        {
            if (State != HostResumeState.Pending)
            {
                return;
            }
            State = HostResumeState.Failed;
            LastError = reason ?? string.Empty;
        }

        /// <summary>Clear all state so a later session can make its own startup decision.</summary>
        public void Reset()
        {
            State = HostResumeState.Idle;
            Attempts = 0;
            LastError = string.Empty;
            deadlineAtMs = 0m;
            nextAttemptAtMs = 0m;
        }

        private void FailDeadline(string reason)
        {
            State = HostResumeState.Failed;
            if (!string.IsNullOrEmpty(reason))
            {
                LastError = "startup resume deadline expired: " + reason;
            }
            else if (!string.IsNullOrEmpty(LastError))
            {
                LastError = "startup resume deadline expired: " + LastError;
            }
            else
            {
                LastError = "startup resume deadline expired";
            }
        }
    }
}
