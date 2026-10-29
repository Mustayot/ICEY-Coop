using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using IceyCoop.Core;
using IceyCoop.Protocol;
using IceyCoop.Transport;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>
    /// Host-side join transaction (T15). Captures a real baseline via
    /// SnapshotAssembler from an injected entity-source provider, buffers
    /// post-baseline events, ships BaselineBegin/Chunk/End and waits for the
    /// client BaselineAck before activating the slot.
    /// </summary>
    public sealed class JoinTransaction
    {
        public delegate List<EntitySource> BaselineProvider();

        /// <summary>
        /// Supplies the G7 story snapshot embedded in the baseline. Injected as a delegate so the
        /// pure test project can compile JoinTransaction without the GameBridge story layer; the
        /// session wires it to StoryCapture.CurrentSnapshotBytes when hosting.
        /// </summary>
        public static Func<byte[]> StorySnapshotProvider;

        public enum State { Idle, SendingBaseline, AwaitingAck, Active, Cancelled }

        private const int MaxBufferedEvents = 512;
        private const uint JoinTimeoutTicks = 10000; // 10s

        private INetworkLink link;
        private uint epoch;
        private BaselineProvider baselineProvider;

        private State state = State.Idle;
        private uint baselineId;
        private ulong includedEventSeq;
        private ulong hostTick;
        private readonly Queue<WorldEventMessage> bufferedEvents = new Queue<WorldEventMessage>();
        private uint startedTicks;
        private string lastError;
        private byte[] baselineDigest;

        public State CurrentState
        {
            get { return state; }
        }

        public ulong IncludedEventSeq
        {
            get { return includedEventSeq; }
        }

        public string LastError
        {
            get { return lastError; }
        }

        public void Begin(INetworkLink link, uint epoch, BaselineProvider provider, ulong baselineIncludedEventSeq, ulong baselineHostTick)
        {
            Begin(link, epoch, provider, baselineIncludedEventSeq, baselineHostTick, 1);
        }

        public void Begin(INetworkLink link, uint epoch, BaselineProvider provider, ulong baselineIncludedEventSeq, ulong baselineHostTick, uint id)
        {
            this.link = link;
            this.epoch = epoch;
            baselineProvider = provider;
            state = State.SendingBaseline;
            baselineId = id;
            includedEventSeq = baselineIncludedEventSeq;
            hostTick = baselineHostTick;
            bufferedEvents.Clear();
            startedTicks = SessionModel.NowTicks();
            lastError = null;
            SendBaseline();
        }

        /// <summary>Buffers one post-baseline world event while the client is
        /// catching up. Returns false when the bounded buffer overflows (the
        /// caller should then request a resync).</summary>
        public bool BufferEvent(WorldEventMessage ev)
        {
            if (state != State.AwaitingAck && state != State.SendingBaseline)
            {
                return false;
            }
            if (bufferedEvents.Count >= MaxBufferedEvents)
            {
                lastError = "event buffer overflow";
                BridgeContext.Error("[t15] " + lastError);
                return false;
            }
            bufferedEvents.Enqueue(ev);
            return true;
        }

        public bool OnBaselineAck(BaselineAckMessage ack)
        {
            if (state != State.AwaitingAck)
            {
                return false;
            }
            if (ack.BaselineId != baselineId || ack.IncludedEventSeq != includedEventSeq || !BytesEqual(ack.AppliedDigest, baselineDigest))
            {
                lastError = "ack baseline id mismatch";
                BridgeContext.Error("[t15] " + lastError);
                return false;
            }
            state = State.Active;
            BridgeContext.Info(string.Format("[t15] activate player slot=2 firstInput={0}", ack.IncludedEventSeq));
            FlushBufferedEvents();
            return true;
        }

        public void Poll(uint nowTicks)
        {
            if (state == State.AwaitingAck && nowTicks - startedTicks >= JoinTimeoutTicks)
            {
                state = State.Cancelled;
                lastError = "join timeout";
                BridgeContext.Error("[t15] join timeout");
            }
        }

        /// <summary>World reset while the join is in flight cancels it; no half
        /// character is left behind.</summary>
        public void CancelOnEpochChange(uint newEpoch)
        {
            if (newEpoch != epoch && state != State.Active && state != State.Idle)
            {
                state = State.Cancelled;
                BridgeContext.Info("[t15] join cancelled (epoch changed)");
            }
        }

        private void SendBaseline()
        {
            SnapshotAssembler assembler = new SnapshotAssembler();
            assembler.IncludedEventSeq = includedEventSeq;
            assembler.HostTick = hostTick;

            List<EntitySource> sources = baselineProvider != null ? baselineProvider() : new List<EntitySource>();
            List<EntitySnapshot> snapshots = assembler.Build(sources);
            List<EntitySnapshot[]> parts = assembler.Slice(snapshots, SnapshotAssembler.ReliableSliceMaxBytes);

            byte[][] chunkData = new byte[parts.Count][];
            uint total = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                chunkData[i] = EncodeEntities(parts[i]);
                total += (uint)chunkData[i].Length;
            }

            byte[] digest = ComputeBaselineDigest(chunkData);
            baselineDigest = digest;

            BaselineBeginMessage begin = new BaselineBeginMessage();
            begin.BaselineId = baselineId;
            begin.Epoch = epoch;
            begin.IncludedEventSeq = includedEventSeq;
            begin.HostTick = hostTick;
            begin.TotalLength = total;
            begin.ChunkCount = (uint)parts.Count;
            begin.Digest = digest;
            Send(MessageKinds.BaselineBegin, begin.Encode());

            for (int i = 0; i < parts.Count; i++)
            {
                BaselineChunkMessage chunk = new BaselineChunkMessage();
                chunk.BaselineId = baselineId;
                chunk.Index = (uint)i;
                chunk.Data = chunkData[i];
                Send(MessageKinds.BaselineChunk, chunk.Encode());
            }

            BaselineEndMessage end = new BaselineEndMessage();
            end.BaselineId = baselineId;
            end.TotalLength = total;
            end.Digest = digest;
            end.StorySnapshot = StorySnapshotProvider != null ? StorySnapshotProvider() : new byte[0];
            Send(MessageKinds.BaselineEnd, end.Encode());

            state = State.AwaitingAck;
            BridgeContext.Info(string.Format("[t15] baseline begin id={0} B={1} entities={2} chunks={3}", baselineId, includedEventSeq, snapshots.Count, parts.Count));
        }

        private void FlushBufferedEvents()
        {
            while (bufferedEvents.Count > 0)
            {
                WorldEventMessage ev = bufferedEvents.Dequeue();
                Send(MessageKinds.WorldEvent, ev.Encode());
            }
        }

        private static byte[] ComputeBaselineDigest(byte[][] chunks)
        {
            int total = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                total += chunks[i].Length;
            }
            byte[] all = new byte[total];
            int offset = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                Array.Copy(chunks[i], 0, all, offset, chunks[i].Length);
                offset += chunks[i].Length;
            }
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] full = sha.ComputeHash(all);
                // Baseline Digest field is 16 bytes on the wire: first 16 of SHA256.
                byte[] digest = new byte[16];
                Array.Copy(full, 0, digest, 0, 16);
                return digest;
            }
        }

        private static byte[] EncodeEntities(EntitySnapshot[] entities)
        {
            ByteWriter w = new ByteWriter();
            int count = entities == null ? 0 : entities.Length;
            w.WriteUInt32((uint)count);
            for (int i = 0; i < count; i++)
            {
                byte[] bytes = EntitySnapshotCodec.Encode(entities[i]);
                if (bytes.Length > ushort.MaxValue)
                {
                    throw new ProtocolException("entity record too large");
                }
                w.WriteUInt16((ushort)bytes.Length);
                w.WriteBytes(bytes);
            }
            return w.ToArray();
        }

        private void Send(ushort kind, byte[] body)
        {
            if (link == null || !link.IsConnected)
            {
                return;
            }
            byte[] frame = new byte[2 + body.Length];
            frame[0] = (byte)(kind & 0xFF);
            frame[1] = (byte)((kind >> 8) & 0xFF);
            Array.Copy(body, 0, frame, 2, body.Length);
            link.SendReliable(frame);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }
}
