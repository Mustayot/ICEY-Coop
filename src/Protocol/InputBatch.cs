using System;

namespace IceyCoop.Protocol
{
    public struct InputBatch
    {
        public byte PlayerId;
        public InputFrame[] Frames;
    }

    /// <summary>
    /// One input packet: player id plus 1-4 input frames (last-received snapshot
    /// is carried separately). Frames are length-prefixed; all axis/edge rules
    /// from InputFrameCodec still apply.
    /// </summary>
    public static class InputBatchCodec
    {
        public const int MaxFramesPerBatch = 4;

        public static void Validate(InputBatch batch)
        {
            if (batch.PlayerId != Core.PlayerIds.P1 && batch.PlayerId != Core.PlayerIds.P2)
            {
                throw new ProtocolException("invalid player id in input batch");
            }
            if (batch.Frames == null || batch.Frames.Length == 0 || batch.Frames.Length > MaxFramesPerBatch)
            {
                throw new ProtocolException("input batch must contain 1-4 frames");
            }
        }

        public static byte[] Encode(InputBatch batch)
        {
            Validate(batch);
            ByteWriter w = new ByteWriter();
            w.WriteByte(batch.PlayerId);
            w.WriteByte((byte)batch.Frames.Length);
            for (int i = 0; i < batch.Frames.Length; i++)
            {
                byte[] frame = InputFrameCodec.Encode(batch.Frames[i]);
                if (frame.Length > ushort.MaxValue)
                {
                    throw new ProtocolException("input frame too large");
                }
                w.WriteUInt16((ushort)frame.Length);
                w.WriteBytes(frame);
            }
            return w.ToArray();
        }

        public static InputBatch Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            InputBatch batch = new InputBatch();
            batch.PlayerId = r.ReadByte();
            byte count = r.ReadByte();
            if (count == 0 || count > MaxFramesPerBatch)
            {
                throw new ProtocolException("invalid frame count in input batch");
            }
            batch.Frames = new InputFrame[count];
            for (int i = 0; i < count; i++)
            {
                ushort length = r.ReadUInt16();
                byte[] frameBytes = r.ReadBytes(length);
                batch.Frames[i] = InputFrameCodec.Decode(frameBytes);
            }
            Validate(batch);
            return batch;
        }
    }
}
