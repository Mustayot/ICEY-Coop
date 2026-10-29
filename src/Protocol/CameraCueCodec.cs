namespace IceyCoop.Protocol
{
    /// <summary>
    /// Camera cue payload: a host-emitted story camera effect that a mid-sequence
    /// joiner replays locally. Mirrors the native CameraController seams
    /// CameraZoom(Vector3 pos, float second, float deltaZ) and
    /// CameraShake(float second, float strength, ShakeTypeEnum type, bool isLoop).
    /// Fixed layout so decode always consumes a known number of bytes.
    /// </summary>
    public struct CameraCuePayload
    {
        public byte Action; // 1=Zoom 2=Shake
        public float A;     // Zoom: pos.x / Shake: second
        public float B;     // Zoom: pos.y / Shake: strength
        public float C;     // Zoom: pos.z / Shake: unused
        public float D;     // Zoom: second / Shake: unused
        public float E;     // Zoom: deltaZ / Shake: unused
        public byte X;      // Shake: type (0=Vertical 1=Horizon 2=Rect); Zoom: must be 0
        public byte Y;      // Shake: isLoop (0/1); Zoom: must be 0
    }

    public static class CameraCueCodec
    {
        public const byte ActionZoom = 1;
        public const byte ActionShake = 2;
        public const byte MaxShakeType = 2;

        public static byte[] Encode(CameraCuePayload p)
        {
            if (p.Action != ActionZoom && p.Action != ActionShake)
            {
                throw new ProtocolException("invalid camera cue action: " + p.Action);
            }
            if (p.X > MaxShakeType)
            {
                throw new ProtocolException("invalid camera cue shake type");
            }
            if (p.Y > 1)
            {
                throw new ProtocolException("invalid camera cue loop flag");
            }

            ByteWriter w = new ByteWriter();
            w.WriteByte(p.Action);
            w.WriteFloat(p.A);
            w.WriteFloat(p.B);
            w.WriteFloat(p.C);
            w.WriteFloat(p.D);
            w.WriteFloat(p.E);
            w.WriteByte(p.X);
            w.WriteByte(p.Y);
            return w.ToArray();
        }

        public static CameraCuePayload Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            CameraCuePayload p = new CameraCuePayload();
            p.Action = r.ReadByte();
            if (p.Action != ActionZoom && p.Action != ActionShake)
            {
                throw new ProtocolException("invalid camera cue action: " + p.Action);
            }
            p.A = r.ReadFloat();
            p.B = r.ReadFloat();
            p.C = r.ReadFloat();
            p.D = r.ReadFloat();
            p.E = r.ReadFloat();
            p.X = r.ReadByte();
            p.Y = r.ReadByte();
            if (p.X > MaxShakeType)
            {
                throw new ProtocolException("invalid camera cue shake type");
            }
            if (p.Y > 1)
            {
                throw new ProtocolException("invalid camera cue loop flag");
            }
            Wire.Finish(r);
            return p;
        }
    }
}
