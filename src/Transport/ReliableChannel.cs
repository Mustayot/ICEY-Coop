using System;
using System.Collections.Generic;

namespace IceyCoop.Transport
{
    public enum ReceiveResult
    {
        Delivered = 0,
        Buffered = 1,
        Duplicate = 2,
        Overflow = 3
    }

    /// <summary>
    /// In-order reliable channel. Out-of-order packets are buffered and flushed
    /// once contiguous; duplicates are dropped; overflow asks for a resync.
    /// </summary>
    public sealed class ReliableChannel
    {
        private readonly SortedDictionary<ulong, byte[]> buffer = new SortedDictionary<ulong, byte[]>();
        private readonly int maxBuffer;

        private ulong nextExpected;

        public ReliableChannel(int maxBuffer)
        {
            if (maxBuffer <= 0)
            {
                throw new ArgumentOutOfRangeException("maxBuffer");
            }
            this.maxBuffer = maxBuffer;
            nextExpected = 1;
        }

        public ulong NextExpected
        {
            get { return nextExpected; }
        }

        public int BufferedCount
        {
            get { return buffer.Count; }
        }

        public ReceiveResult Receive(ulong seq, byte[] payload, out List<byte[]> delivered)
        {
            delivered = new List<byte[]>();
            if (seq == 0)
            {
                return ReceiveResult.Duplicate; // seq 0 is not a valid data sequence
            }
            if (seq < nextExpected)
            {
                return ReceiveResult.Duplicate;
            }
            if (seq == nextExpected)
            {
                delivered.Add(payload);
                nextExpected++;
                while (buffer.ContainsKey(nextExpected))
                {
                    delivered.Add(buffer[nextExpected]);
                    buffer.Remove(nextExpected);
                    nextExpected++;
                }
                return ReceiveResult.Delivered;
            }

            // seq > nextExpected
            if (buffer.ContainsKey(seq))
            {
                return ReceiveResult.Duplicate; // already buffered: not new, no overwrite, no overflow
            }
            if (buffer.Count >= maxBuffer)
            {
                return ReceiveResult.Overflow;
            }
            buffer[seq] = payload;
            return ReceiveResult.Buffered;
        }

        public void Reset(ulong newNextExpected)
        {
            if (newNextExpected == 0)
            {
                throw new ArgumentOutOfRangeException("newNextExpected");
            }
            buffer.Clear();
            nextExpected = newNextExpected;
        }
    }
}
