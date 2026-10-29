using System;
using System.Collections.Generic;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class TargetResolverSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "TargetResolver";

            s.Tests.Add(new TestCase
            {
                Name = "nearest valid candidate chosen",
                Run = delegate
                {
                    TargetResolver resolver = new TargetResolver(0, 0f);
                    List<TargetCandidate> list = new List<TargetCandidate>
                    {
                        C(1, 10f, 0f),
                        C(2, 2f, 0f)
                    };
                    Assert.Equal((ulong)2, resolver.Resolve(0f, 0f, list, 0), "nearest");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "dead and untargetable excluded",
                Run = delegate
                {
                    TargetResolver resolver = new TargetResolver(0, 0f);
                    TargetCandidate dead = C(1, 0f, 0f); dead.Alive = false;
                    TargetCandidate untargetable = C(2, 1f, 0f); untargetable.Targetable = false;
                    TargetCandidate ok = C(3, 5f, 0f);
                    List<TargetCandidate> list = new List<TargetCandidate> { dead, untargetable, ok };
                    Assert.Equal((ulong)3, resolver.Resolve(0f, 0f, list, 0), "only valid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "cooldown prevents immediate switch",
                Run = delegate
                {
                    TargetResolver resolver = new TargetResolver(5, 0f);
                    List<TargetCandidate> list = new List<TargetCandidate>
                    {
                        C(1, 10f, 0f),
                        C(2, 20f, 0f)
                    };
                    Assert.Equal((ulong)1, resolver.Resolve(0f, 0f, list, 0), "first target");
                    list[1].X = 1f; // a much nearer target appears
                    Assert.Equal((ulong)1, resolver.Resolve(0f, 0f, list, 1), "cooldown holds current");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "stickiness keeps close current target",
                Run = delegate
                {
                    TargetResolver resolver = new TargetResolver(0, 2f);
                    List<TargetCandidate> list = new List<TargetCandidate>
                    {
                        C(1, 10f, 0f),
                        C(2, 20f, 0f)
                    };
                    Assert.Equal((ulong)1, resolver.Resolve(0f, 0f, list, 0), "first target");
                    // candidate 2 moves to 9.5: nearer but only by 0.5, within stickiness
                    list[1].X = 9.5f;
                    Assert.Equal((ulong)1, resolver.Resolve(0f, 0f, list, 1), "stick to current");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "clears to zero when nothing valid",
                Run = delegate
                {
                    TargetResolver resolver = new TargetResolver(0, 0f);
                    List<TargetCandidate> list = new List<TargetCandidate> { C(1, 5f, 0f) };
                    resolver.Resolve(0f, 0f, list, 0);
                    list[0].Alive = false;
                    Assert.Equal((ulong)0, resolver.Resolve(0f, 0f, list, 0), "cleared");
                }
            });

            return s;
        }

        private static TargetCandidate C(ulong id, float x, float y)
        {
            TargetCandidate c = new TargetCandidate();
            c.EntityId = id;
            c.X = x;
            c.Y = y;
            c.Alive = true;
            c.Targetable = true;
            return c;
        }
    }
}
