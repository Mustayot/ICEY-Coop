using System;

namespace IceyCoop.Core
{
    /// <summary>Which recovery transaction is currently allowed to touch the world.</summary>
    public enum RecoveryOwner
    {
        /// <summary>Nobody: the world is free.</summary>
        None = 0,

        /// <summary>HostStartupResume is loading and applying a co-op save at host boot.</summary>
        StartupResume = 1,

        /// <summary>The team-wipe native reload plus its checkpoint re-apply.</summary>
        WipeRestore = 2,

        /// <summary>PartnerRevive is bringing one downed player back.</summary>
        PartnerRevive = 3
    }

    /// <summary>
    /// T20 arbitration point (see docs/G5-T20-队友恢复设计-2026-10-23.md section 3.6).
    ///
    /// By round 49 there were already three things that could move the world back to a saved
    /// state, each guarded by its own ad-hoc flag: the startup resume gate, the wipe
    /// coordinator's single native reload, and the checkpoint re-apply that follows it. Adding
    /// partner revive as a fourth made the ad-hoc arrangement unsafe: two transactions can
    /// plausibly run in the same frame and write over each other.
    ///
    /// This is the single place that answers "who owns recovery right now". It is a plain
    /// instance class with a shared <see cref="Default"/> so the specs can drive their own
    /// instance instead of touching global state.
    ///
    /// Pure logic: no Unity, no clock.
    /// </summary>
    public sealed class RecoveryArbiter
    {
        /// <summary>The process-wide arbiter used by the bridge.</summary>
        public static readonly RecoveryArbiter Default = new RecoveryArbiter();

        /// <summary>Who currently owns recovery (None when free).</summary>
        public RecoveryOwner Current { get; private set; }

        /// <summary>Successful claims (diagnostics).</summary>
        public int Claims { get; private set; }

        /// <summary>Refused claims (diagnostics).</summary>
        public int Refusals { get; private set; }

        /// <summary>The owner that refused the most recent claim (diagnostics).</summary>
        public RecoveryOwner BlockedBy { get; private set; }

        public bool IsFree
        {
            get { return Current == RecoveryOwner.None; }
        }

        public bool IsHeldBy(RecoveryOwner owner)
        {
            return owner != RecoveryOwner.None && Current == owner;
        }

        /// <summary>True when <paramref name="owner"/> must not act right now.</summary>
        public bool IsHeldByAnother(RecoveryOwner owner)
        {
            return Current != RecoveryOwner.None && Current != owner;
        }

        /// <summary>
        /// Takes recovery ownership. Idempotent for the current holder (a repeated claim by the
        /// same owner succeeds) so a caller inside its own transaction cannot deadlock itself.
        /// </summary>
        public bool TryClaim(RecoveryOwner owner)
        {
            if (owner == RecoveryOwner.None)
            {
                throw new ArgumentOutOfRangeException("owner", owner, "cannot claim ownership as None");
            }
            if (Current == owner)
            {
                return true;
            }
            if (Current != RecoveryOwner.None)
            {
                Refusals++;
                BlockedBy = Current;
                return false;
            }
            Current = owner;
            Claims++;
            BlockedBy = RecoveryOwner.None;
            return true;
        }

        /// <summary>Gives ownership back. Only the holder can release.</summary>
        public bool Release(RecoveryOwner owner)
        {
            if (owner == RecoveryOwner.None || Current != owner)
            {
                return false;
            }
            Current = RecoveryOwner.None;
            return true;
        }

        /// <summary>Force-free the world at a session boundary.</summary>
        public void Reset()
        {
            Current = RecoveryOwner.None;
            Claims = 0;
            Refusals = 0;
            BlockedBy = RecoveryOwner.None;
        }

        /// <summary>Stable name for the log line.</summary>
        public static string NameOf(RecoveryOwner owner)
        {
            switch (owner)
            {
                case RecoveryOwner.None: return "none";
                case RecoveryOwner.StartupResume: return "startup-resume";
                case RecoveryOwner.WipeRestore: return "wipe-restore";
                case RecoveryOwner.PartnerRevive: return "partner-revive";
                default: return owner.ToString();
            }
        }
    }
}
