using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class EntityRegistrySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "EntityRegistry";

            s.Tests.Add(new TestCase
            {
                Name = "dynamic ids cannot collide with reserved player ids",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry(2);
                    bool isNew;
                    ulong a = reg.Register(1, 1, "enemy:first", 0, out isNew);
                    ulong b = reg.Register(1, 1, "enemy:second", 0, out isNew);
                    Assert.Equal((ulong)3, a, "P1=1 and P2=2 stay reserved");
                    Assert.Equal((ulong)4, b, "next enemy stays distinct");
                    EntityRecord record;
                    Assert.Equal(false, reg.TryGet(1, out record), "no enemy record replaces P1");
                    Assert.Equal(false, reg.TryGet(2, out record), "no enemy record replaces P2");
                }
            });
            s.Tests.Add(new TestCase
            {
                Name = "reserved range cannot wrap into player ids at overflow",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry(ulong.MaxValue);
                    Assert.Throws<InvalidOperationException>(delegate { bool isNew; reg.Register(1, 1, "enemy", 0, out isNew); }, "reserved range cannot wrap");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "register allocates increasing ids and stores record",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    bool isNew;
                    ulong a = reg.Register(1, 100, "level17:BattleCheckPoint", 3, out isNew);
                    Assert.True(isNew, "isNew");
                    Assert.Equal((ulong)1, a, "first id");
                    ulong b = reg.Register(1, 100, "preset", 0, out isNew);
                    Assert.Equal((ulong)2, b, "second id");

                    EntityRecord ra;
                    EntityRecord rb;
                    Assert.True(reg.TryGet(a, out ra), "a found");
                    Assert.True(reg.TryGet(b, out rb), "b found");
                    Assert.Equal((uint)1, ra.WorldEpoch, "epoch");
                    Assert.Equal(100, ra.Kind, "kind");
                    Assert.Equal((uint)3, ra.Generation, "generation");
                    Assert.Equal(false, ra.Despawned, "alive");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "despawn tombstone marks record and updates revision",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    bool isNew;
                    ulong id = reg.Register(1, 1, "preset", 0, out isNew);
                    Assert.True(reg.MarkDespawn(id, 7), "mark");
                    EntityRecord r;
                    Assert.True(reg.TryGet(id, out r), "still queryable");
                    Assert.Equal(true, r.Despawned, "tombstone");
                    Assert.Equal((ulong)7, r.Revision, "revision");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "despawn is idempotent and unknown id returns false",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    bool isNew;
                    ulong id = reg.Register(1, 1, "preset", 0, out isNew);
                    Assert.True(reg.MarkDespawn(id, 3), "first mark");
                    Assert.True(reg.MarkDespawn(id, 9), "second mark idempotent");
                    EntityRecord r;
                    reg.TryGet(id, out r);
                    Assert.Equal((ulong)9, r.Revision, "last revision wins");
                    Assert.Equal(true, reg.MarkDespawn(id, 10), "still markable");
                    Assert.Equal(false, reg.MarkDespawn(999, 1), "unknown id");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "generation bumps per encounter key",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    Assert.Equal((uint)1, reg.BumpGeneration("a"), "a1");
                    Assert.Equal((uint)2, reg.BumpGeneration("a"), "a2");
                    Assert.Equal((uint)1, reg.BumpGeneration("b"), "b1");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "sourceKey and fields queryable through TryGet",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    bool isNew;
                    ulong id = reg.Register(5, 42, "story:E13P3:AirRobot", 2, out isNew);
                    EntityRecord r;
                    Assert.True(reg.TryGet(id, out r), "found");
                    Assert.Equal("story:E13P3:AirRobot", r.SourceKey, "sourceKey");
                    Assert.Equal(42, r.Kind, "kind");
                    Assert.Equal((uint)5, r.WorldEpoch, "epoch");
                    Assert.Equal((uint)2, r.Generation, "generation");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "count reflects registrations and tombstones stay counted",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    Assert.Equal(0, reg.Count, "empty");
                    bool isNew;
                    ulong a = reg.Register(1, 1, "preset", 0, out isNew);
                    ulong b = reg.Register(1, 1, "preset", 0, out isNew);
                    Assert.Equal(2, reg.Count, "two registrations");
                    reg.MarkDespawn(a, 1);
                    Assert.Equal(2, reg.Count, "tombstone retained");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "tryget returns false for unknown id",
                Run = delegate
                {
                    EntityRegistry reg = new EntityRegistry();
                    EntityRecord r;
                    Assert.Equal(false, reg.TryGet(12345, out r), "unknown");
                }
            });

            return s;
        }
    }
}
