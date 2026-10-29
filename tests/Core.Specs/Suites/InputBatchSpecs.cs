using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class InputBatchSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "InputBatch";

            s.Tests.Add(new TestCase
            {
                Name = "round trip with two frames",
                Run = delegate
                {
                    InputBatch batch = new InputBatch();
                    batch.PlayerId = Core.PlayerIds.P2;
                    batch.Frames = new InputFrame[2];
                    batch.Frames[0].InputSeq = 1;
                    batch.Frames[0].Horizontal = 0.5f;
                    batch.Frames[0].Vertical = -0.5f;
                    batch.Frames[0].Edges = new InputEdge[1] { new InputEdge { Ordinal = 0, Action = 2, Pressed = true } };
                    batch.Frames[1].InputSeq = 2;
                    batch.Frames[1].Horizontal = 1f;
                    batch.Frames[1].Edges = new InputEdge[0];

                    InputBatch back = InputBatchCodec.Decode(InputBatchCodec.Encode(batch));
                    Assert.Equal(batch.PlayerId, back.PlayerId, "player");
                    Assert.Equal(2, back.Frames.Length, "frame count");
                    Assert.Equal(batch.Frames[0].InputSeq, back.Frames[0].InputSeq, "seq0");
                    Assert.Equal(true, back.Frames[0].Edges[0].Pressed, "edge0");
                    Assert.Equal(batch.Frames[1].Horizontal, back.Frames[1].Horizontal, "axis1");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid player id rejected",
                Run = delegate
                {
                    InputBatch batch = new InputBatch();
                    batch.PlayerId = 3;
                    batch.Frames = new InputFrame[1];
                    batch.Frames[0].Edges = new InputEdge[0];
                    Assert.Throws<ProtocolException>(delegate { InputBatchCodec.Encode(batch); }, "player 3 invalid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "zero or five frames rejected",
                Run = delegate
                {
                    InputBatch zero = new InputBatch();
                    zero.PlayerId = Core.PlayerIds.P1;
                    zero.Frames = new InputFrame[0];
                    Assert.Throws<ProtocolException>(delegate { InputBatchCodec.Encode(zero); }, "zero frames");

                    InputBatch five = new InputBatch();
                    five.PlayerId = Core.PlayerIds.P1;
                    five.Frames = new InputFrame[5];
                    for (int i = 0; i < 5; i++) { five.Frames[i].Edges = new InputEdge[0]; }
                    Assert.Throws<ProtocolException>(delegate { InputBatchCodec.Encode(five); }, "five frames");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "truncated batch throws",
                Run = delegate
                {
                    Assert.Throws<ProtocolException>(delegate { InputBatchCodec.Decode(new byte[] { 1, 1, 0 }); }, "short read");
                }
            });

            return s;
        }
    }
}
