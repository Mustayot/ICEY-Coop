using System;

namespace IceyCoop.Protocol
{
    public struct InputEdge
    {
        public byte Ordinal;
        public byte Action;
        public bool Pressed; // true = Pressed, false = Released
    }

    /// <summary>One input sample. Axes must be finite and within [-1,1].</summary>
    public struct InputFrame
    {
        public ulong InputSeq;
        public float Horizontal;
        public float Vertical;
        public ushort HeldBits;
        public InputEdge[] Edges;
    }

    public static class InputFrameCodec
    {
        public const int MaxEdgesPerFrame = 16;

        public static void Validate(InputFrame frame)
        {
            if (float.IsNaN(frame.Horizontal) || float.IsInfinity(frame.Horizontal))
            {
                throw new ProtocolException("horizontal axis non-finite");
            }
            if (float.IsNaN(frame.Vertical) || float.IsInfinity(frame.Vertical))
            {
                throw new ProtocolException("vertical axis non-finite");
            }
            if (frame.Horizontal < -1f || frame.Horizontal > 1f || frame.Vertical < -1f || frame.Vertical > 1f)
            {
                throw new ProtocolException("axis out of [-1,1]");
            }
            if (frame.Edges != null && frame.Edges.Length > MaxEdgesPerFrame)
            {
                throw new ProtocolException("too many edges in one frame");
            }
        }

        public static byte[] Encode(InputFrame frame)
        {
            Validate(frame);
            ByteWriter w = new ByteWriter();
            w.WriteUInt64(frame.InputSeq);
            w.WriteFloat(frame.Horizontal);
            w.WriteFloat(frame.Vertical);
            w.WriteUInt16(frame.HeldBits);
            int edgeCount = frame.Edges == null ? 0 : frame.Edges.Length;
            w.WriteByte((byte)edgeCount);
            for (int i = 0; i < edgeCount; i++)
            {
                w.WriteByte(frame.Edges[i].Ordinal);
                w.WriteByte(frame.Edges[i].Action);
                w.WriteByte(frame.Edges[i].Pressed ? (byte)1 : (byte)0);
            }
            return w.ToArray();
        }

        public static InputFrame Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            InputFrame frame = new InputFrame();
            frame.InputSeq = r.ReadUInt64();
            frame.Horizontal = r.ReadFloat();
            frame.Vertical = r.ReadFloat();
            frame.HeldBits = r.ReadUInt16();
            byte edgeCount = r.ReadByte();
            if (edgeCount > MaxEdgesPerFrame)
            {
                throw new ProtocolException("too many edges in frame");
            }
            frame.Edges = new InputEdge[edgeCount];
            for (int i = 0; i < edgeCount; i++)
            {
                frame.Edges[i].Ordinal = r.ReadByte();
                frame.Edges[i].Action = r.ReadByte();
                byte pressed = r.ReadByte();
                if (pressed > 1)
                {
                    throw new ProtocolException("edge pressed flag must be 0 or 1");
                }
                frame.Edges[i].Pressed = pressed == 1;
            }
            Validate(frame);
            return frame;
        }
    }
}
