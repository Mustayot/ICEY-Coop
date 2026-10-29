using System.Text;

namespace IceyCoop.Protocol
{
    public struct EffectTargetStep
    {
        public string Name;
        public ushort Occurrence;
    }

    /// <summary>
    /// Effect cue payload: the host reports one native EffectController.Generate call
    /// and the client replays it locally, so the client actually sees sword trails,
    /// charge glows, execution bursts, hit sparks and enemy explosions.
    ///
    /// Generate is the single choke point for every dynamically spawned visual in the
    /// game (EffectController.Generate(int effectId, Transform target, Vector3
    /// position, Vector3 rotation, Vector3 scale, bool useFxZNum)), so one hook covers
    /// players, enemies, bosses and skills alike.
    ///
    /// Fixed base followed by a bounded, length-prefixed target path (protocol v4).
    /// </summary>
    public struct EffectCuePayload
    {
        /// <summary>Native effect table id (EffectController generalises over it).</summary>
        public int EffectId;
        /// <summary>The entity the effect is parented to, 0 = spawn in world space.
        /// The client maps it to its own replica of that entity so the effect follows
        /// the mirror, which is as close to the host's parenting as a puppet gets.</summary>
        public ulong TargetEntityId;
        public float X;
        public float Y;
        public float Z;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float ScaleX;
        public float ScaleY;
        public float ScaleZ;
        /// <summary>Native useFxZNum flag (0/1): the effect may consume an fx z slot.</summary>
        public byte UseFxZNum;
        /// <summary>How the three position floats must be read. Native Generate treats
        /// its position argument as a WORLD position only when target is null; with a
        /// target it is a local offset (isFollow) or target.position + offset. Mixing the
        /// two put client effects at origin+offset - metres away from the character.
        /// 0 = absolute world position (spawn with no target),
        /// 1 = offset relative to TargetEntityId (spawn parented to that mirror).</summary>
        public byte AnchorMode;
        /// <summary>1 when the native post-generate code mirrored the effect on X
        /// (PlayerAnimEventListener.PlayEffect flips localScale.x by -Sign(player scale)).
        /// Replayed verbatim on the client, otherwise sword effects face the wrong way.</summary>
        public byte FlipX;
        /// <summary>Null or empty means the target entity root; otherwise each step
        /// selects a child by exact name and its zero-based same-name occurrence.</summary>
        public EffectTargetStep[] TargetPath;
    }

    public static class EffectCueCodec
    {
        /// <summary>Absolute world position, spawn with no parent.</summary>
        public const byte AnchorWorld = 0;
        /// <summary>Offset relative to the target entity's mirror.</summary>
        public const byte AnchorTarget = 1;

        public const int MaxTargetDepth = 16;
        public const int MaxTargetNameBytes = 128;
        public const int MaxTargetPathBytes = 1024;
        public const ushort MaxTargetOccurrence = 255;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Legacy 51-byte fields plus the target-path depth byte. Paths add
        /// bounded UTF-8 name/occurrence steps after this base.</summary>
        public const int EncodedLength = 4 + 8 + (9 * 4) + 3 + 1;

        private static byte[] EncodeTargetName(string name)
        {
            if (name == null)
            {
                throw new ProtocolException("effect cue target path name is null");
            }
            byte[] raw;
            try
            {
                raw = StrictUtf8.GetBytes(name);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ProtocolException("effect cue target path name is not valid UTF-8: " + ex.Message);
            }
            if (raw.Length == 0 || raw.Length > MaxTargetNameBytes)
            {
                throw new ProtocolException("effect cue target path name byte length is outside 1.." + MaxTargetNameBytes);
            }
            return raw;
        }

        private static int TargetPathDepth(EffectCuePayload p)
        {
            return p.TargetPath == null ? 0 : p.TargetPath.Length;
        }

        private static void ValidateTargetPath(EffectCuePayload p)
        {
            int depth = TargetPathDepth(p);
            if (depth > MaxTargetDepth)
            {
                throw new ProtocolException("effect cue target path depth exceeds " + MaxTargetDepth);
            }
            if (p.AnchorMode == AnchorWorld && depth != 0)
            {
                throw new ProtocolException("world effect cue cannot carry a target path");
            }

            int pathBytes = 1;
            for (int i = 0; i < depth; i++)
            {
                byte[] name = EncodeTargetName(p.TargetPath[i].Name);
                if (p.TargetPath[i].Occurrence > MaxTargetOccurrence)
                {
                    throw new ProtocolException("effect cue target path occurrence exceeds " + MaxTargetOccurrence);
                }
                pathBytes += 2 + name.Length + 2;
                if (pathBytes > MaxTargetPathBytes)
                {
                    throw new ProtocolException("effect cue encoded target path exceeds " + MaxTargetPathBytes + " bytes");
                }
            }
        }

        private static void Validate(EffectCuePayload p)
        {
            if (p.EffectId < 0)
            {
                throw new ProtocolException("effect cue id must not be negative: " + p.EffectId);
            }
            if (p.UseFxZNum > 1)
            {
                throw new ProtocolException("invalid effect cue useFxZNum flag");
            }
            if (p.FlipX > 1)
            {
                throw new ProtocolException("invalid effect cue flip flag");
            }
            if (p.AnchorMode > AnchorTarget)
            {
                throw new ProtocolException("invalid effect cue anchor mode: " + p.AnchorMode);
            }
            if (p.AnchorMode == AnchorTarget && p.TargetEntityId == 0)
            {
                throw new ProtocolException("effect cue anchored to a target needs a target entity id");
            }
            ValidateTargetPath(p);
        }

        public static byte[] Encode(EffectCuePayload p)
        {
            Validate(p);

            ByteWriter w = new ByteWriter();
            w.WriteUInt32((uint)p.EffectId);
            w.WriteUInt64(p.TargetEntityId);
            w.WriteFloat(p.X);
            w.WriteFloat(p.Y);
            w.WriteFloat(p.Z);
            w.WriteFloat(p.RotX);
            w.WriteFloat(p.RotY);
            w.WriteFloat(p.RotZ);
            w.WriteFloat(p.ScaleX);
            w.WriteFloat(p.ScaleY);
            w.WriteFloat(p.ScaleZ);
            w.WriteByte(p.UseFxZNum);
            w.WriteByte(p.AnchorMode);
            w.WriteByte(p.FlipX);
            int depth = TargetPathDepth(p);
            w.WriteByte((byte)depth);
            for (int i = 0; i < depth; i++)
            {
                byte[] name = EncodeTargetName(p.TargetPath[i].Name);
                w.WriteUInt16((ushort)name.Length);
                w.WriteBytes(name);
                w.WriteUInt16(p.TargetPath[i].Occurrence);
            }
            return w.ToArray();
        }

        public static EffectCuePayload Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            EffectCuePayload p = new EffectCuePayload();
            p.EffectId = (int)r.ReadUInt32();
            p.TargetEntityId = r.ReadUInt64();
            p.X = r.ReadFloat();
            p.Y = r.ReadFloat();
            p.Z = r.ReadFloat();
            p.RotX = r.ReadFloat();
            p.RotY = r.ReadFloat();
            p.RotZ = r.ReadFloat();
            p.ScaleX = r.ReadFloat();
            p.ScaleY = r.ReadFloat();
            p.ScaleZ = r.ReadFloat();
            p.UseFxZNum = r.ReadByte();
            p.AnchorMode = r.ReadByte();
            p.FlipX = r.ReadByte();
            int depth = r.ReadByte();
            if (depth > MaxTargetDepth)
            {
                throw new ProtocolException("effect cue target path depth exceeds " + MaxTargetDepth);
            }
            if (depth > 0)
            {
                p.TargetPath = new EffectTargetStep[depth];
            }
            int pathBytes = 1;
            for (int i = 0; i < depth; i++)
            {
                int nameLength = r.ReadUInt16();
                if (nameLength == 0 || nameLength > MaxTargetNameBytes)
                {
                    throw new ProtocolException("effect cue target path name byte length is outside 1.." + MaxTargetNameBytes);
                }
                pathBytes += 2 + nameLength + 2;
                if (pathBytes > MaxTargetPathBytes)
                {
                    throw new ProtocolException("effect cue encoded target path exceeds " + MaxTargetPathBytes + " bytes");
                }
                byte[] rawName = r.ReadBytes(nameLength);
                string name;
                try
                {
                    name = StrictUtf8.GetString(rawName);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new ProtocolException("effect cue target path name is not valid UTF-8: " + ex.Message);
                }
                ushort occurrence = r.ReadUInt16();
                if (occurrence > MaxTargetOccurrence)
                {
                    throw new ProtocolException("effect cue target path occurrence exceeds " + MaxTargetOccurrence);
                }
                p.TargetPath[i] = new EffectTargetStep { Name = name, Occurrence = occurrence };
            }
            Validate(p);
            Wire.Finish(r);
            return p;
        }
    }
}
