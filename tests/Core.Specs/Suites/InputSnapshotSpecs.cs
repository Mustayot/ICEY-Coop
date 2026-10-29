using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class InputSnapshotSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "InputSnapshot";

            s.Tests.Add(new TestCase
            {
                Name = "held persists, clear removes",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.SetHeld(3, true);
                    Assert.True(snap.IsHeld(3), "held");
                    snap.SetHeld(3, false);
                    Assert.True(!snap.IsHeld(3), "released");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "opposite edges in one frame both survive",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(2, true);
                    snap.RecordEdge(2, false); // opposite direction must not clobber press
                    Assert.True(snap.ConsumePressed(2), "pressed survived");
                    Assert.True(snap.ConsumeReleased(2), "released survived");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "same-direction duplicate consumes once",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(3, true);
                    snap.RecordEdge(3, true); // duplicate collapses
                    Assert.True(snap.ConsumePressed(3), "pressed once");
                    Assert.True(!snap.ConsumePressed(3), "not again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "consume is once per frame",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(4, true);
                    Assert.True(snap.ConsumePressed(4), "first");
                    Assert.True(!snap.ConsumePressed(4), "second blocked");
                    snap.ClearTransient();
                    snap.RecordEdge(4, true);
                    Assert.True(snap.ConsumePressed(4), "next frame");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "released edge",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(5, false);
                    Assert.True(snap.ConsumeReleased(5), "released once");
                    Assert.True(!snap.ConsumeReleased(5), "not again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "cross-frame transient reset allows re-consume",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(7, false);
                    Assert.True(snap.ConsumeReleased(7), "first frame");
                    snap.ClearTransient();
                    snap.RecordEdge(7, false);
                    Assert.True(snap.ConsumeReleased(7), "next frame released");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "release all held for control lock",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.SetHeld(1, true);
                    snap.SetHeld(2, true);
                    snap.ReleaseAllHeld();
                    Assert.Equal(0, snap.HeldCount, "all released");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "held and edges are orthogonal",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.SetHeld(1, true);
                    snap.SetHeld(2, true);
                    snap.RecordEdge(3, true);
                    snap.ReleaseAllHeld();
                    Assert.Equal(0, snap.HeldCount, "held cleared");
                    Assert.True(!snap.IsHeld(1), "held 1 cleared");
                    Assert.True(snap.ConsumePressed(3), "edge survives release all held");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ordinal dedup: same ordinal once, different ordinals separate",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(5, true, 1);
                    snap.RecordEdge(5, true, 1); // replayed packet ignored
                    Assert.True(snap.ConsumePressed(5), "pressed once");
                    Assert.True(!snap.ConsumePressed(5), "not again");
                    snap.RecordEdge(5, false, 2); // different ordinal registers release
                    Assert.True(snap.ConsumeReleased(5), "released via distinct ordinal");
                }
            });

            return s;
        }
    }
}
