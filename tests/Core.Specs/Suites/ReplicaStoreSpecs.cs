using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class ReplicaStoreSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ReplicaStore";

            s.Tests.Add(new TestCase
            {
                Name = "baseline then ordered events apply",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[] { Spawn(100, 1, 1) }), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 6, Spawn(101, 1, 2)), "event 6");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 7, Spawn(102, 1, 3)), "event 7");
                    Assert.Equal((ulong)7, store.AppliedEventSeq, "applied seq");
                    Assert.Equal(3, store.EntityCount, "entity count");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "gap event rejected",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[0]), "baseline");
                    Assert.Equal(ApplyResult.GapEvent, store.ApplyEvent(1, 8, Spawn(100, 1, 1)), "gap");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "stale event rejected",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[0]), "baseline");
                    Assert.Equal(ApplyResult.StaleEvent, store.ApplyEvent(1, 5, Spawn(100, 1, 1)), "stale");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "revision rollback rejected",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[] { Spawn(100, 21, 1) }), "baseline");
                    Assert.Equal(ApplyResult.RevisionRollback, store.ApplyEvent(1, 6, Spawn(100, 20, 1)), "rollback");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "despawn tombstone cannot be revived",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[] { Spawn(100, 1, 1) }), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 6, Despawn(100, 2)), "despawn");
                    Assert.Equal(ApplyResult.TombstoneConflict, store.ApplyEvent(1, 7, Spawn(100, 3, 1)), "revive rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "old snapshot cannot revive despawned entity",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[] { Spawn(100, 1, 1) }), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 6, Despawn(100, 2)), "despawn");
                    EntityRecord[] stale = new EntityRecord[] { Spawn(100, 1, 1) };
                    Assert.Equal(ApplyResult.TombstoneConflict, store.ApplySnapshot(1, 6, stale), "old snapshot rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "wrong epoch rejected",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[0]), "baseline");
                    Assert.Equal(ApplyResult.WrongEpoch, store.ApplyEvent(2, 6, Spawn(100, 1, 1)), "wrong epoch");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "snapshot with newer revision applies",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 5, new EntityRecord[] { Spawn(100, 1, 1) }), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplySnapshot(1, 8, new EntityRecord[] { Spawn(100, 2, 1), Spawn(101, 1, 2) }), "snapshot");
                    Assert.Equal((ulong)5, store.AppliedEventSeq, "cursor stays at baseline");
                    EntityRecord r;
                    store.TryGetEntity(100, out r);
                    Assert.Equal((ulong)2, r.Revision, "revision updated");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "snapshot does not advance event cursor",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 0, new EntityRecord[0]), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplySnapshot(1, 2, new EntityRecord[] { Spawn(100, 1, 1) }), "snapshot");
                    Assert.Equal((ulong)0, store.AppliedEventSeq, "cursor stays at baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 1, Spawn(101, 1, 2)), "event 1 applies after snapshot");
                    Assert.Equal((ulong)1, store.AppliedEventSeq, "cursor advanced by event");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "baseline epoch rollback rejected",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(2, 0, new EntityRecord[0]), "first baseline");
                    Assert.Equal(ApplyResult.WrongEpoch, store.ApplyBaseline(1, 0, new EntityRecord[0]), "epoch rollback");
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(2, 0, new EntityRecord[0]), "same epoch ok");
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(3, 0, new EntityRecord[0]), "epoch advance ok");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "old despawn revision is rollback and leaves entity alive",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 0, new EntityRecord[] { Spawn(100, 21, 1) }), "baseline");
                    Assert.Equal(ApplyResult.RevisionRollback, store.ApplyEvent(1, 1, Despawn(100, 20)), "old despawn rejected");
                    EntityRecord r;
                    Assert.True(store.TryGetEntity(100, out r), "entity exists");
                    Assert.True(!r.Despawned, "still alive");
                    Assert.Equal((ulong)21, r.Revision, "revision unchanged");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "despawn with newer revision still tombstones",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 0, new EntityRecord[] { Spawn(100, 20, 1) }), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 1, Despawn(100, 21)), "despawn applied");
                    EntityRecord r;
                    Assert.True(store.TryGetEntity(100, out r), "entity exists");
                    Assert.True(r.Despawned, "tombstoned");
                    Assert.Equal((ulong)21, r.Revision, "revision updated");
                }
            });

            return s;
        }

        private static EntityRecord Spawn(ulong id, ulong revision, int kind)
        {
            return new EntityRecord(id, revision, kind, false);
        }

        private static EntityRecord Despawn(ulong id, ulong revision)
        {
            return new EntityRecord(id, revision, 0, true);
        }
    }
}
