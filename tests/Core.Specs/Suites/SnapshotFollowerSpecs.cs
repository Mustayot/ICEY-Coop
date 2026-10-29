using System;
using System.Collections;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class SnapshotFollowerSpecs
    {
        private const float FrameSeconds = 1f / 60f;

        public Suite Build()
        {
            Suite suite = new Suite { Name = "SnapshotFollower" };
            Add(suite, "first target snaps into place", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                Assert.Equal(false, follower.HasTarget, "starts without a target");
                Assert.Equal(true, follower.SetTarget(0f, 0f, 1f, 0f), "first sample snaps");
                Assert.Equal(true, follower.HasTarget, "target recorded");
                Assert.Equal(1f, follower.TargetX, "target x");
            });
            Add(suite, "twelve rendered frames all advance toward the target", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                Assert.Equal(true, follower.SetTarget(0f, 0f, 1f, 0f), "snap");
                float x = 1f;
                float y = 0f;
                // A second snapshot lands one metre away: the visual must keep
                // moving after the packet, not only on the packet.
                Assert.Equal(false, follower.SetTarget(x, y, 0f, 0f), "one metre does not snap");
                int moving = 0;
                float previous = x;
                for (int i = 0; i < 12; i++)
                {
                    follower.Step(FrameSeconds, ref x, ref y);
                    if (x != previous)
                    {
                        moving++;
                    }
                    Assert.True(x <= previous, "moves monotonically toward the target");
                    previous = x;
                }
                Assert.Equal(12, moving, "every rendered frame advances");
                Assert.True(x > 0f && x < 1f, "does not reach the target in 200ms");
            });
            Add(suite, "a target beyond the snap distance is placed, not eased", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                Assert.Equal(true, follower.SetTarget(0f, 0f, 1f, 0f), "snap");
                Assert.Equal(true, follower.SetTarget(1f, 0f, 9f, 0f), "eight metres snaps");
                Assert.Equal(true, follower.SetTarget(9f, 0f, 2f, 5f), "seven metres snaps");
            });
            Add(suite, "zero delta never moves the visual", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                Assert.Equal(true, follower.SetTarget(0f, 0f, 1f, 0f), "snap");
                Assert.Equal(false, follower.SetTarget(1f, 0f, 0f, 0f), "one metre does not snap");
                float x = 1f;
                float y = 0f;
                follower.Step(0f, ref x, ref y);
                Assert.Equal(1f, x, "unchanged");
            });
            Add(suite, "alpha clamps to one so a long frame cannot overshoot", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                Assert.Equal(true, follower.SetTarget(0f, 0f, 1f, 0f), "snap");
                Assert.Equal(false, follower.SetTarget(1f, 0f, 0f, 0f), "one metre does not snap");
                float x = 1f;
                float y = 0f;
                follower.Step(5f, ref x, ref y);
                Assert.Equal(0f, x, "landed exactly on the target");
                Assert.Equal(1f, SnapshotFollower.Alpha(10f), "alpha clamps");
                Assert.Equal(0f, SnapshotFollower.Alpha(-1f), "negative delta is ignored");
            });
            Add(suite, "vertical offset is followed independently", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                Assert.Equal(true, follower.SetTarget(0f, 0f, 0f, 0f), "snap");
                Assert.Equal(false, follower.SetTarget(0f, 0f, 0f, 1.5f), "1.5m does not snap");
                float x = 0f;
                float y = 0f;
                follower.Step(1f, ref x, ref y);
                Assert.Equal(0f, x, "x untouched");
                Assert.Equal(1.5f, y, "y followed");
            });
            Add(suite, "clear drops the recorded target", delegate
            {
                SnapshotFollower follower = new SnapshotFollower();
                follower.SetTarget(0f, 0f, 1f, 0f);
                follower.Clear();
                Assert.Equal(false, follower.HasTarget, "no target after clear");
                float x = 5f;
                float y = 6f;
                follower.Step(FrameSeconds, ref x, ref y);
                Assert.Equal(5f, x, "nothing to follow");
            });
            Add(suite, "huge distance arithmetic stays finite", delegate
            {
                Assert.Equal(5f, SnapshotFollower.Distance(0f, 0f, 3f, 4f), "3-4-5 triangle");
                Assert.Equal(0f, SnapshotFollower.Distance(1f, 1f, 1f, 1f), "same point");
            });
            return suite;
        }

        private static void Add(Suite suite, string name, Action run)
        {
            suite.Tests.Add(new TestCase { Name = name, Run = run });
        }
    }
}
