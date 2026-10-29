using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class CameraCueSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CameraCue";

            s.Tests.Add(new TestCase
            {
                Name = "zoom round trip preserves position, duration and delta z",
                Run = delegate
                {
                    CameraCuePayload p = new CameraCuePayload();
                    p.Action = CameraCueCodec.ActionZoom;
                    p.A = 12.5f;
                    p.B = -3.25f;
                    p.C = -10f;
                    p.D = 1.5f;
                    p.E = 3f;
                    CameraCuePayload back = CameraCueCodec.Decode(CameraCueCodec.Encode(p));
                    Assert.Equal(CameraCueCodec.ActionZoom, back.Action, "action");
                    Assert.Equal(12.5f, back.A, "x");
                    Assert.Equal(-3.25f, back.B, "y");
                    Assert.Equal(-10f, back.C, "z");
                    Assert.Equal(1.5f, back.D, "second");
                    Assert.Equal(3f, back.E, "delta z");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "shake round trip preserves time strength type and loop",
                Run = delegate
                {
                    CameraCuePayload p = new CameraCuePayload();
                    p.Action = CameraCueCodec.ActionShake;
                    p.A = 0.3f;
                    p.B = 0.16666667f;
                    p.X = 2;
                    p.Y = 1;
                    CameraCuePayload back = CameraCueCodec.Decode(CameraCueCodec.Encode(p));
                    Assert.Equal(CameraCueCodec.ActionShake, back.Action, "action");
                    Assert.Equal(0.3f, back.A, "second");
                    Assert.Equal(0.16666667f, back.B, "strength");
                    Assert.Equal(2, (int)back.X, "type");
                    Assert.Equal(1, (int)back.Y, "loop");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid action rejected",
                Run = delegate
                {
                    CameraCuePayload p = new CameraCuePayload();
                    p.Action = 9;
                    Assert.Throws<ProtocolException>(delegate { CameraCueCodec.Encode(p); }, "encode action 9");
                    Assert.Throws<ProtocolException>(delegate { CameraCueCodec.Decode(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }); }, "decode action 9");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "shake type and loop bounds rejected",
                Run = delegate
                {
                    CameraCuePayload badType = new CameraCuePayload();
                    badType.Action = CameraCueCodec.ActionShake;
                    badType.X = 3;
                    Assert.Throws<ProtocolException>(delegate { CameraCueCodec.Encode(badType); }, "type 3");

                    CameraCuePayload badLoop = new CameraCuePayload();
                    badLoop.Action = CameraCueCodec.ActionShake;
                    badLoop.Y = 2;
                    Assert.Throws<ProtocolException>(delegate { CameraCueCodec.Encode(badLoop); }, "loop 2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "trailing bytes are rejected",
                Run = delegate
                {
                    CameraCuePayload p = new CameraCuePayload();
                    p.Action = CameraCueCodec.ActionZoom;
                    p.E = 3f;
                    byte[] bytes = CameraCueCodec.Encode(p);
                    byte[] padded = new byte[bytes.Length + 1];
                    Array.Copy(bytes, padded, bytes.Length);
                    Assert.Throws<ProtocolException>(delegate { CameraCueCodec.Decode(padded); }, "trailing byte");
                }
            });

            return s;
        }
    }
}
