using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class EffectCueSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "EffectCue";

            s.Tests.Add(new TestCase
            {
                Name = "round trip preserves id, target entity and the three vectors",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = 155;
                    p.TargetEntityId = 4096;
                    p.X = 12.5f;
                    p.Y = -3.25f;
                    p.Z = -0.2f;
                    p.RotX = 1f;
                    p.RotY = 2f;
                    p.RotZ = 180f;
                    p.ScaleX = -1f;
                    p.ScaleY = 1f;
                    p.ScaleZ = 1f;
                    p.UseFxZNum = 1;
                    p.AnchorMode = EffectCueCodec.AnchorTarget;
                    p.FlipX = 1;

                    byte[] bytes = EffectCueCodec.Encode(p);
                    Assert.Equal(EffectCueCodec.EncodedLength, bytes.Length, "encoded length matches the declared constant");
                    Assert.Equal(52, bytes.Length, "root cue uses the v4 base length");

                    EffectCuePayload back = EffectCueCodec.Decode(bytes);
                    Assert.Equal(155, back.EffectId, "effect id");
                    Assert.Equal(4096UL, back.TargetEntityId, "target entity");
                    Assert.Equal(12.5f, back.X, "x");
                    Assert.Equal(-3.25f, back.Y, "y");
                    Assert.Equal(-0.2f, back.Z, "z");
                    Assert.Equal(1f, back.RotX, "rot x");
                    Assert.Equal(2f, back.RotY, "rot y");
                    Assert.Equal(180f, back.RotZ, "rot z");
                    Assert.Equal(-1f, back.ScaleX, "scale x");
                    Assert.Equal(1f, back.ScaleY, "scale y");
                    Assert.Equal(1f, back.ScaleZ, "scale z");
                    Assert.Equal(1, (int)back.UseFxZNum, "useFxZNum");
                    Assert.Equal((int)EffectCueCodec.AnchorTarget, (int)back.AnchorMode, "anchor mode");
                    Assert.Equal(1, (int)back.FlipX, "flip flag");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "world-space cue keeps target entity 0 and the fx flag 0",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = 7;
                    p.TargetEntityId = 0;
                    p.AnchorMode = EffectCueCodec.AnchorWorld;
                    EffectCuePayload back = EffectCueCodec.Decode(EffectCueCodec.Encode(p));
                    Assert.Equal(0UL, back.TargetEntityId, "no target");
                    Assert.Equal(0, (int)back.UseFxZNum, "flag clear");
                    Assert.Equal(0, (int)back.AnchorMode, "world anchor");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "anchoring to a target without an entity id is rejected",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = 5;
                    p.TargetEntityId = 0;
                    p.AnchorMode = EffectCueCodec.AnchorTarget;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "target anchor with no entity");

                    EffectCuePayload bad = new EffectCuePayload();
                    bad.EffectId = 5;
                    bad.AnchorMode = 9;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(bad); }, "anchor mode 9");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "negative effect id rejected on both sides",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = -1;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "encode negative id");

                    byte[] raw = new byte[EffectCueCodec.EncodedLength];
                    raw[0] = 0xFF;
                    raw[1] = 0xFF;
                    raw[2] = 0xFF;
                    raw[3] = 0xFF;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(raw); }, "decode negative id");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "fx flag above one rejected",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = 3;
                    p.UseFxZNum = 2;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "encode flag 2");

                    byte[] bytes = EffectCueCodec.Encode(new EffectCuePayload());
                    bytes[48] = 2;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(bytes); }, "decode flag 2");

                    byte[] flipBytes = EffectCueCodec.Encode(new EffectCuePayload());
                    flipBytes[50] = 2;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(flipBytes); }, "decode flip 2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "trailing bytes and truncation are rejected",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = 11;
                    byte[] bytes = EffectCueCodec.Encode(p);

                    byte[] padded = new byte[bytes.Length + 1];
                    Array.Copy(bytes, padded, bytes.Length);
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(padded); }, "trailing byte");

                    byte[] shortBuffer = new byte[bytes.Length - 1];
                    Array.Copy(bytes, shortBuffer, shortBuffer.Length);
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(shortBuffer); }, "truncated payload");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "legacy 51-byte effect cue is rejected",
                Run = delegate
                {
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(new byte[51]); },
                        "v3 effect cue must not decode as v4");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "child path round trip preserves names with delimiters and occurrences",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.EffectId = 8;
                    p.TargetEntityId = 4;
                    p.AnchorMode = EffectCueCodec.AnchorTarget;
                    p.TargetPath = new EffectTargetStep[]
                    {
                        new EffectTargetStep { Name = "Arm/Right:Grip", Occurrence = 2 },
                        new EffectTargetStep { Name = "Body", Occurrence = 255 }
                    };
                    byte[] bytes = EffectCueCodec.Encode(p);
                    EffectCuePayload back = EffectCueCodec.Decode(bytes);
                    Assert.Equal(2, back.TargetPath.Length, "path depth");
                    Assert.Equal("Arm/Right:Grip", back.TargetPath[0].Name, "separator characters stay inside a name");
                    Assert.Equal((ushort)2, back.TargetPath[0].Occurrence, "same-name occurrence");
                    Assert.Equal("Body", back.TargetPath[1].Name, "second path name");
                    Assert.Equal((ushort)255, back.TargetPath[1].Occurrence, "maximum occurrence");
                    Assert.Equal(52 + 2 + 14 + 2 + 2 + 4 + 2, bytes.Length, "path bytes are appended after the v4 base fields");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "target path writer enforces depth name occurrence and cumulative byte bounds",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload { AnchorMode = EffectCueCodec.AnchorTarget, TargetEntityId = 1 };
                    p.TargetPath = new EffectTargetStep[EffectCueCodec.MaxTargetDepth + 1];
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "depth above maximum");

                    p.TargetPath = new EffectTargetStep[] { new EffectTargetStep { Name = "", Occurrence = 0 } };
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "empty name");
                    p.TargetPath = new EffectTargetStep[] { new EffectTargetStep { Name = null, Occurrence = 0 } };
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "null name");
                    p.TargetPath = new EffectTargetStep[] { new EffectTargetStep { Name = new string('x', EffectCueCodec.MaxTargetNameBytes + 1), Occurrence = 0 } };
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "name over byte limit");
                    p.TargetPath = new EffectTargetStep[] { new EffectTargetStep { Name = "Bone", Occurrence = 256 } };
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "occurrence above maximum");

                    p.TargetPath = new EffectTargetStep[EffectCueCodec.MaxTargetDepth];
                    for (int i = 0; i < p.TargetPath.Length; i++)
                    {
                        p.TargetPath[i] = new EffectTargetStep { Name = new string('n', EffectCueCodec.MaxTargetNameBytes), Occurrence = 0 };
                    }
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "cumulative path bytes above maximum");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "target path decoder rejects malformed bounds and truncation",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload { AnchorMode = EffectCueCodec.AnchorTarget, TargetEntityId = 1 };
                    p.TargetPath = new EffectTargetStep[] { new EffectTargetStep { Name = "Bone", Occurrence = 0 } };
                    byte[] valid = EffectCueCodec.Encode(p);

                    byte[] badDepth = (byte[])valid.Clone();
                    badDepth[51] = (byte)(EffectCueCodec.MaxTargetDepth + 1);
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(badDepth); }, "decode excessive depth");

                    byte[] emptyName = (byte[])valid.Clone();
                    emptyName[52] = 0;
                    emptyName[53] = 0;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(emptyName); }, "decode empty name");

                    byte[] longName = (byte[])valid.Clone();
                    longName[52] = 129;
                    longName[53] = 0;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(longName); }, "decode name over limit");

                    byte[] badOccurrence = (byte[])valid.Clone();
                    badOccurrence[badOccurrence.Length - 2] = 0;
                    badOccurrence[badOccurrence.Length - 1] = 1;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(badOccurrence); }, "decode occurrence over limit");

                    byte[] invalidUtf8 = (byte[])valid.Clone();
                    invalidUtf8[54] = 0xFF;
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(invalidUtf8); }, "decode invalid UTF-8 name");

                    byte[] shortPath = new byte[valid.Length - 1];
                    Array.Copy(valid, shortPath, shortPath.Length);
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Decode(shortPath); }, "truncated path step");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "world anchor rejects nonempty target path",
                Run = delegate
                {
                    EffectCuePayload p = new EffectCuePayload();
                    p.AnchorMode = EffectCueCodec.AnchorWorld;
                    p.TargetPath = new EffectTargetStep[] { new EffectTargetStep { Name = "Body", Occurrence = 0 } };
                    Assert.Throws<ProtocolException>(delegate { EffectCueCodec.Encode(p); }, "world anchor path");

                    p.TargetPath = new EffectTargetStep[0];
                    byte[] root = EffectCueCodec.Encode(p);
                    Assert.Equal(52, root.Length, "empty path remains a root target cue");
                }
            });

            return s;
        }
    }
}
