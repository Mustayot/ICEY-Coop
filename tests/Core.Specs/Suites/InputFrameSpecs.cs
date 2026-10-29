using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class InputFrameSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "InputFrame";

            s.Tests.Add(new TestCase
            {
                Name = "round trip with edges",
                Run = delegate
                {
                    InputFrame f = new InputFrame();
                    f.InputSeq = 42;
                    f.Horizontal = 0.5f;
                    f.Vertical = -1f;
                    f.HeldBits = 0x00FF;
                    f.Edges = new InputEdge[]
                    {
                        new InputEdge { Ordinal = 0, Action = 3, Pressed = true },
                        new InputEdge { Ordinal = 1, Action = 3, Pressed = false }
                    };
                    InputFrame back = InputFrameCodec.Decode(InputFrameCodec.Encode(f));
                    Assert.Equal(f.InputSeq, back.InputSeq, "seq");
                    Assert.Equal(f.Horizontal, back.Horizontal, "horizontal");
                    Assert.Equal(f.HeldBits, back.HeldBits, "held");
                    Assert.Equal(2, back.Edges.Length, "edge count");
                    Assert.Equal(true, back.Edges[0].Pressed, "edge0 pressed");
                    Assert.Equal(false, back.Edges[1].Pressed, "edge1 released");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "axis out of range rejected",
                Run = delegate
                {
                    InputFrame f = new InputFrame();
                    f.Horizontal = 1.5f;
                    f.Vertical = 0f;
                    f.Edges = new InputEdge[0];
                    Assert.Throws<ProtocolException>(delegate { InputFrameCodec.Encode(f); }, "horizontal out of range");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "NaN axis rejected",
                Run = delegate
                {
                    InputFrame f = new InputFrame();
                    f.Horizontal = float.NaN;
                    f.Vertical = 0f;
                    f.Edges = new InputEdge[0];
                    Assert.Throws<ProtocolException>(delegate { InputFrameCodec.Encode(f); }, "NaN must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "too many edges rejected",
                Run = delegate
                {
                    InputFrame f = new InputFrame();
                    f.Horizontal = 0f;
                    f.Vertical = 0f;
                    f.Edges = new InputEdge[InputFrameCodec.MaxEdgesPerFrame + 1];
                    Assert.Throws<ProtocolException>(delegate { InputFrameCodec.Encode(f); }, "too many edges must throw");
                }
            });

            return s;
        }
    }
}
