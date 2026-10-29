using System;

namespace IceyCoop.Core
{
    public enum SlotState
    {
        /// <summary>Nobody has ever used this slot.</summary>
        Empty = 0,
        /// <summary>A peer is playing right now.</summary>
        Occupied = 1,
        /// <summary>The peer dropped; the slot is held open until the deadline.</summary>
        Grace = 2,
        /// <summary>The grace window expired and the slot was surrendered.</summary>
        Released = 3
    }

    /// <summary>
    /// T22 host-side reconnect window: when the peer drops mid-fight the host must not tear the
    /// world down immediately. The slot stays reserved for a short grace period so the same peer
    /// can come back into the running session instead of triggering a fresh join.
    ///
    /// Pure logic on purpose - the runtime wiring (link, tickets, baseline) sits on top and the
    /// decisions here are the part that must never depend on timing luck.
    /// </summary>
    public sealed class ReconnectWindow
    {
        public const uint DefaultGraceMs = 20000;

        private readonly uint graceMs;
        private SlotState state = SlotState.Empty;
        private uint generation;
        private uint graceStartedAtMs;

        public ReconnectWindow() : this(DefaultGraceMs)
        {
        }

        public ReconnectWindow(uint graceMs)
        {
            if (graceMs == 0) throw new ArgumentOutOfRangeException("graceMs");
            this.graceMs = graceMs;
        }

        public SlotState State
        {
            get { return state; }
        }

        public uint Generation
        {
            get { return generation; }
        }

        public uint GraceMs
        {
            get { return graceMs; }
        }

        /// <summary>A peer took the slot (fresh join).</summary>
        public void Occupy(uint nowMs)
        {
            AdvanceGeneration();
            state = SlotState.Occupied;
            graceStartedAtMs = nowMs;
        }

        /// <summary>The peer dropped: hold the slot open for the grace window.</summary>
        public void PeerLost(uint nowMs)
        {
            if (state != SlotState.Occupied && state != SlotState.Grace) return;
            state = SlotState.Grace;
            graceStartedAtMs = nowMs;
        }

        /// <summary>Checks whether a resume is still within grace without changing slot state.</summary>
        public bool CanResume(uint nowMs)
        {
            return state == SlotState.Grace && unchecked(nowMs - graceStartedAtMs) <= graceMs;
        }

        /// <summary>
        /// A peer wants the slot back. Accepted only while the window is still open; the
        /// generation is bumped so stale input from the previous connection cannot land.
        /// </summary>
        public bool TryResume(uint nowMs)
        {
            if (!CanResume(nowMs)) return false;
            AdvanceGeneration();
            state = SlotState.Occupied;
            return true;
        }

        /// <summary>Reattach an already authenticated, unconfirmed admission.
        /// Caller must validate its pending credential and original session/epoch.
        /// Reusing this generation never extends the original grace deadline.</summary>
        public bool ResumePending(uint expectedGeneration, uint nowMs)
        {
            if (generation != expectedGeneration || !CanResume(nowMs)) return false;
            state = SlotState.Occupied;
            return true;
        }

        /// <summary>Advances the clock. Returns true exactly once, when the window gives up.</summary>
        public bool Poll(uint nowMs)
        {
            if (state == SlotState.Grace && unchecked(nowMs - graceStartedAtMs) > graceMs)
            {
                state = SlotState.Released;
                return true;
            }
            return false;
        }

        /// <summary>Remaining time, 0 when not in the grace window.</summary>
        public uint RemainingMs(uint nowMs)
        {
            if (state != SlotState.Grace) return 0;
            uint elapsed = unchecked(nowMs - graceStartedAtMs);
            return elapsed >= graceMs ? 0 : graceMs - elapsed;
        }

        private void AdvanceGeneration()
        {
            if (generation == uint.MaxValue)
            {
                throw new InvalidOperationException("Connection generation exhausted.");
            }
            generation++;
        }
    }
}
