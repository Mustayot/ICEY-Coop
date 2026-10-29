using System;
using System.Reflection;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ReconnectWindowSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ReconnectWindow";

            s.Tests.Add(new TestCase
            {
                Name = "a drop inside the window can be resumed",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow(20000);
                    w.Occupy(1000);
                    uint gen = w.Generation;
                    w.PeerLost(5000);
                    Assert.Equal(SlotState.Grace, w.State, "grace");
                    Assert.True(w.TryResume(15000), "resume inside window");
                    Assert.Equal(SlotState.Occupied, w.State, "occupied again");
                    Assert.True(w.Generation > gen, "generation bumped so stale input is rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "resuming after the deadline is refused",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow(1000);
                    w.Occupy(0);
                    w.PeerLost(100);
                    Assert.True(!w.TryResume(2000), "too late");
                    Assert.Equal(SlotState.Grace, w.State, "still grace until polled");
                    Assert.True(w.Poll(2001), "poll releases");
                    Assert.Equal(SlotState.Released, w.State, "released");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "release fires once and remaining time shrinks",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow(5000);
                    w.Occupy(0);
                    w.PeerLost(1000);
                    Assert.Equal((uint)5000, w.RemainingMs(1000), "full window at drop");
                    Assert.Equal((uint)3000, w.RemainingMs(3000), "counts down");
                    Assert.True(!w.Poll(5000), "not expired at the deadline");
                    Assert.True(w.Poll(6001), "expires after it");
                    Assert.True(!w.Poll(7000), "only fires once");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "grace deadline and remaining time handle uint wrap",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow(2000);
                    uint lostAt = uint.MaxValue - 500;
                    w.Occupy(lostAt - 100);
                    w.PeerLost(lostAt);

                    Assert.Equal((uint)2000, w.RemainingMs(lostAt), "full grace at drop time");
                    Assert.Equal((uint)1500, w.RemainingMs(uint.MaxValue), "remaining before wrap");
                    Assert.Equal((uint)1499, w.RemainingMs(0), "remaining after wrap");
                    Assert.True(!w.Poll(1499), "not released at deadline");
                    Assert.True(w.Poll(1500), "released after wrapped deadline");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "default grace remains twenty seconds",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow();
                    Assert.Equal((uint)20000, w.GraceMs, "default grace");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "generation exhaustion throws without changing slot state",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow(20000);
                    w.Occupy(0);
                    w.PeerLost(1000);
                    FieldInfo generation = typeof(ReconnectWindow).GetField("generation", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.True(generation != null, "generation field exists");
                    generation.SetValue(w, uint.MaxValue);

                    Assert.Throws<InvalidOperationException>(delegate { w.Occupy(2000); }, "occupy must not wrap generation");
                    Assert.Equal(SlotState.Grace, w.State, "failed occupy leaves state unchanged");
                    Assert.Equal(uint.MaxValue, w.Generation, "failed occupy leaves generation unchanged");
                    Assert.Throws<InvalidOperationException>(delegate { w.TryResume(2000); }, "resume must not wrap generation");
                    Assert.Equal(SlotState.Grace, w.State, "failed resume leaves state unchanged");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a fresh join starts a new generation and closes the window",
                Run = delegate
                {
                    ReconnectWindow w = new ReconnectWindow(5000);
                    w.Occupy(0);
                    w.PeerLost(100);
                    w.Occupy(200);
                    Assert.Equal(SlotState.Occupied, w.State, "occupied");
                    Assert.Equal((uint)0, w.RemainingMs(200), "no grace left");
                    Assert.True(!w.TryResume(300), "nothing to resume");
                }
            });

            return s;
        }
    }
}
