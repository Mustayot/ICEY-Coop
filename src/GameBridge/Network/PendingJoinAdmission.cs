using System;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>One preActive admission, bounded by its original deadline. Until
    /// baseline acknowledgement, a detached peer may retry either its old disk
    /// credential or the replacement Welcome credential without another generation.
    /// This class never authenticates a first attempt; ResumeGate must do that.</summary>
    public sealed class PendingJoinAdmission
    {
        private byte[] oldTicket;
        private byte[] replacement;
        private byte[] sessionId;
        private uint epoch;
        private uint generation;
        private uint startedAt;
        private uint timeout;
        private bool armed;
        private bool attached;
        public uint GraceStartedAt { get { return startedAt; } }
        public bool IsPending { get { return oldTicket != null; } }
        public byte[] ReplacementTicket { get { return replacement == null ? null : (byte[])replacement.Clone(); } }
        public void BeginFresh(uint now, uint timeout)
        {
            Confirm(); startedAt = now; this.timeout = timeout; armed = true; attached = true;
        }
        public void BeginResume(byte[] oldTicket, byte[] replacement, byte[] sessionId, uint epoch, uint generation, uint graceStartedAt, uint graceMs)
        {
            if (oldTicket == null || oldTicket.Length != 16 || replacement == null || replacement.Length != 16 || sessionId == null || sessionId.Length != 16)
                throw new ArgumentException("invalid pending resume credential metadata");
            this.oldTicket = (byte[])oldTicket.Clone(); this.replacement = (byte[])replacement.Clone();
            this.sessionId = (byte[])sessionId.Clone(); this.epoch = epoch; this.generation = generation;
            startedAt = graceStartedAt; timeout = graceMs; armed = true; attached = true;
        }
        public void Detach() { attached = false; }
        public void Confirm()
        {
            oldTicket = null; replacement = null; sessionId = null; armed = false; attached = false;
        }
        public bool IsExpired(uint now) { return armed && unchecked(now - startedAt) > timeout; }
        public bool TryRetry(byte[] ticket, byte[] sessionId, uint epoch, uint generation, uint now)
        {
            if (!IsPending || attached || IsExpired(now) || epoch != this.epoch || generation != this.generation ||
                !Equal(sessionId, this.sessionId) || (!Equal(ticket, oldTicket) && !Equal(ticket, replacement))) return false;
            attached = true;
            return true;
        }
        private static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
