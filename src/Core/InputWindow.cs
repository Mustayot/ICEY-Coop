using System;

namespace IceyCoop.Core
{
    public enum InputAckResult
    {
        Applied = 0,
        Duplicate = 1,
        Stale = 2
    }

    /// <summary>
    /// Host-side input window for one player. Accepts monotonically increasing
    /// input sequence numbers only; replayed/stale packets are rejected so a
    /// duplicate packet can never register a duplicate edge. Per-frame edge
    /// ordinal dedup is handled by InputSnapshot.RecordEdge(ordinal).
    /// </summary>
    public sealed class InputWindow
    {
        private ulong lastAcceptedSeq;
        private ulong lastAckedSeq;

        public ulong LastAcceptedSeq
        {
            get { return lastAcceptedSeq; }
        }

        public ulong LastAckedSeq
        {
            get { return lastAckedSeq; }
        }

        public bool Accept(byte playerId, ulong inputSeq)
        {
            if (playerId == 0)
            {
                throw new ArgumentException("playerId");
            }
            if (inputSeq == 0)
            {
                return false;
            }
            if (inputSeq > lastAcceptedSeq)
            {
                lastAcceptedSeq = inputSeq;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Acks in order. A sequence at or below LastAckedSeq is Duplicate, above
        /// LastAcceptedSeq is Stale, otherwise it advances the ack cursor.
        /// </summary>
        public bool TryAck(ulong seq, out InputAckResult result)
        {
            if (seq <= lastAckedSeq)
            {
                result = InputAckResult.Duplicate;
                return false;
            }
            if (seq > lastAcceptedSeq)
            {
                result = InputAckResult.Stale;
                return false;
            }
            lastAckedSeq = seq;
            result = InputAckResult.Applied;
            return true;
        }

        public void Reset()
        {
            lastAcceptedSeq = 0;
            lastAckedSeq = 0;
        }
    }
}
