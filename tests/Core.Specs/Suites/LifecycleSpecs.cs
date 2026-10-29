using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class LifecycleSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Lifecycle";

            s.Tests.Add(new TestCase
            {
                Name = "actor model defaults",
                Run = delegate
                {
                    ActorModel a = new ActorModel();
                    Assert.Equal(LifeState.Alive, a.Life, "alive");
                    Assert.Equal(ConnectionState.Disconnected, a.Connection, "disconnected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "directive defaults to allow and round-trips",
                Run = delegate
                {
                    LifecycleCoordinator c = new LifecycleCoordinator();
                    Assert.Equal(LifecycleDirective.Allow, c.GetDirective(PlayerIds.P1), "default allow");
                    c.SetDirective(PlayerIds.P2, LifecycleDirective.SuppressAuthority);
                    Assert.Equal(LifecycleDirective.SuppressAuthority, c.GetDirective(PlayerIds.P2), "set suppress");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "deferred start runs once until reset",
                Run = delegate
                {
                    LifecycleCoordinator c = new LifecycleCoordinator();
                    Assert.True(c.TryRunDeferredStart(PlayerIds.P1), "first");
                    Assert.True(!c.TryRunDeferredStart(PlayerIds.P1), "second blocked");
                    c.ResetDeferredStart(PlayerIds.P1);
                    Assert.True(c.TryRunDeferredStart(PlayerIds.P1), "after reset");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "activation generation is per player monotonic",
                Run = delegate
                {
                    LifecycleCoordinator c = new LifecycleCoordinator();
                    Assert.Equal((uint)1, c.NextActivationGeneration(PlayerIds.P1), "p1 gen1");
                    Assert.Equal((uint)2, c.NextActivationGeneration(PlayerIds.P1), "p1 gen2");
                    Assert.Equal((uint)1, c.NextActivationGeneration(PlayerIds.P2), "p2 gen1");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid player id rejected",
                Run = delegate
                {
                    LifecycleCoordinator c = new LifecycleCoordinator();
                    Assert.Throws<ArgumentOutOfRangeException>(delegate { c.SetDirective(0, LifecycleDirective.Allow); }, "0 invalid");
                }
            });

            return s;
        }
    }
}
