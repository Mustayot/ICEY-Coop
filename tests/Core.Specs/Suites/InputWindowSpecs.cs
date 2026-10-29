using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class InputWindowSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "InputWindow";

            s.Tests.Add(new TestCase
            {
                Name = "accepts increasing seq and rejects old or zero",
                Run = delegate
                {
                    InputWindow w = new InputWindow();
                    Assert.True(w.Accept(1, 5), "first");
                    Assert.True(w.Accept(1, 6), "next");
                    Assert.True(!w.Accept(1, 6), "replay rejected");
                    Assert.True(!w.Accept(1, 4), "old rejected");
                    Assert.True(!w.Accept(1, 0), "zero rejected");
                    Assert.Equal((ulong)6, w.LastAcceptedSeq, "last accepted");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ack applied duplicate and stale",
                Run = delegate
                {
                    InputWindow w = new InputWindow();
                    w.Accept(1, 3);
                    w.Accept(1, 5);
                    InputAckResult r;
                    Assert.True(w.TryAck(3, out r) && r == InputAckResult.Applied, "first applied");
                    Assert.True(w.TryAck(4, out r) && r == InputAckResult.Applied, "gap ack applied");
                    Assert.True(!w.TryAck(4, out r) && r == InputAckResult.Duplicate, "duplicate");
                    Assert.True(!w.TryAck(9, out r) && r == InputAckResult.Stale, "stale above accepted");
                    Assert.True(w.TryAck(5, out r) && r == InputAckResult.Applied, "final applied");
                    Assert.Equal((ulong)5, w.LastAckedSeq, "last acked");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reset clears cursors",
                Run = delegate
                {
                    InputWindow w = new InputWindow();
                    w.Accept(1, 10);
                    w.Reset();
                    Assert.Equal((ulong)0, w.LastAcceptedSeq, "accepted reset");
                    Assert.Equal((ulong)0, w.LastAckedSeq, "acked reset");
                    Assert.True(w.Accept(1, 1), "reuse after reset");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid player id rejected",
                Run = delegate
                {
                    InputWindow w = new InputWindow();
                    Assert.Throws<ArgumentException>(delegate { w.Accept(0, 1); }, "player id 0");
                }
            });

            return s;
        }
    }
}
