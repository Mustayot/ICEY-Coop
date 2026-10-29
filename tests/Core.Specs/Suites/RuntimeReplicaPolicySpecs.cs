using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class RuntimeReplicaPolicySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "RuntimeReplicaPolicy";

            s.Tests.Add(new TestCase
            {
                Name = "reliable hit events, equal-cursor split snapshots, and later hits stay in sequence",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(2, 10,
                        new EntityRecord[] { Record(42, 0, 1) }), "baseline");
                    SnapshotFreshnessGate gate = NewGate(2, 100, 10);

                    WorldEventMessage firstHit = Wire(Hit(11, 42, 1));
                    EntityRecord firstHitRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, firstHit, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal((ulong)1, firstHitRecord.Revision, "event revision is kept");
                    Assert.Equal((int)1, firstHitRecord.Kind, "existing kind is kept");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(2, firstHit.EventSeq, firstHitRecord), "first hit event");

                    StateSnapshotMessage part0 = Wire(Snapshot(1, 101, 11, 0, 2, Entity(42, 1)));
                    StateSnapshotMessage part1 = Wire(Snapshot(1, 101, 11, 1, 2, Entity(43, 0)));
                    Assert.True(RuntimeReplicaPolicy.CanPresentSnapshot(store, part0), "snapshot cursor matches");
                    Assert.True(gate.TryAcceptPart(part0), "split part zero accepted");
                    Assert.Equal(ApplyResult.Applied, ApplyOne(store, part0), "part zero record merges");
                    Assert.True(RuntimeReplicaPolicy.CanPresentSnapshot(store, part1), "same cursor remains current");
                    Assert.True(gate.TryAcceptPart(part1), "same snapshot part one accepted");
                    Assert.Equal(ApplyResult.Applied, ApplyOne(store, part1), "part one record merges");
                    Assert.Equal((ulong)11, store.AppliedEventSeq, "snapshot does not advance event cursor");

                    WorldEventMessage secondHit = Wire(Hit(12, 42, 2));
                    EntityRecord secondHitRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, secondHit, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal((ulong)2, secondHitRecord.Revision, "next hit advances from stored revision");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(2, secondHit.EventSeq, secondHitRecord), "second hit does not break reliable stream");
                    Assert.Equal((ulong)12, store.AppliedEventSeq, "event cursor continues");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "UDP snapshot ahead of TCP cursor is deferred without consuming its gate id",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(3, 20,
                        new EntityRecord[] { Record(50, 0, 1) }), "baseline");
                    SnapshotFreshnessGate gate = NewGate(3, 200, 20);
                    StateSnapshotMessage overtaking = Wire(Snapshot(7, 201, 21, 0, 1, Entity(50, 1)));

                    Assert.True(!RuntimeReplicaPolicy.CanPresentSnapshot(store, overtaking), "future event cursor deferred");
                    Assert.Equal((ulong)0, gate.LatestSnapshotId, "deferred packet never reaches gate");

                    WorldEventMessage hit = Wire(Hit(21, 50, 1));
                    EntityRecord hitRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, hit, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(3, hit.EventSeq, hitRecord), "reliable event catches cursor up");
                    Assert.True(RuntimeReplicaPolicy.CanPresentSnapshot(store, overtaking), "next same-cursor snapshot can present");

                    StateSnapshotMessage retry = Wire(Snapshot(8, 202, 21, 0, 1, Entity(50, 1)));
                    Assert.True(RuntimeReplicaPolicy.CanPresentSnapshot(store, retry), "next host sample is eligible");
                    Assert.True(gate.TryAcceptPart(retry), "next host sample advances gate");
                    Assert.Equal(ApplyResult.Applied, ApplyOne(store, retry), "next sample merges");
                    Assert.Equal((ulong)21, store.AppliedEventSeq, "cursor remains caught up");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "snapshot behind the applied event cursor is not presented",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 4,
                        new EntityRecord[] { Record(60, 0, 1) }), "baseline");
                    WorldEventMessage event5 = Wire(Hit(5, 60, 1));
                    EntityRecord eventRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, event5, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 5, eventRecord), "event advances cursor");
                    Assert.True(!RuntimeReplicaPolicy.CanPresentSnapshot(store,
                        Wire(Snapshot(1, 10, 4, 0, 1, Entity(60, 0)))), "old event cursor rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "midfight baseline revision is preserved for the next hit event",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(5, 30,
                        new EntityRecord[] { Record(70, 5, 1) }), "hot-join baseline");
                    WorldEventMessage hit = Wire(Hit(31, 70, 6));
                    EntityRecord eventRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, hit, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal((ulong)6, eventRecord.Revision, "new event revision wins");
                    Assert.Equal((int)1, eventRecord.Kind, "baseline entity kind survives");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(5, 31, eventRecord), "midfight hit event applies");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "entity-less event generations stay on the zero-revision sentinel",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 0,
                        new EntityRecord[] { Record(0, 0, -1) }), "baseline");
                    WorldEventMessage encounter = Wire(Event(1, (byte)WorldEventSubType.EncounterState, 0, 99, new byte[0]));
                    EntityRecord encounterRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, encounter, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal((ulong)0, encounterRecord.Revision, "generation is not sentinel revision");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 1, encounterRecord), "encounter event applies");

                    WorldEventMessage objectState = Wire(Event(2, (byte)WorldEventSubType.ObjectState, 0, 0, new byte[] { 1 }));
                    EntityRecord objectRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, objectState, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 2, objectRecord), "later zero-revision sentinel event applies");
                    Assert.Equal((ulong)2, store.AppliedEventSeq, "sentinel does not break cursor");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unknown non-snapshot entity event keeps its event revision fallback",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(1, 10, new EntityRecord[0]), "baseline");
                    WorldEventMessage event11 = Wire(Hit(11, 99, 4));
                    EntityRecord record = RuntimeReplicaPolicy.CreateEventRecord(
                        store, event11, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal((ulong)4, record.Revision, "unknown target uses event revision");
                    Assert.Equal((int)-1, record.Kind, "unknown target keeps unknown kind");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(1, 11, record), "unknown event preserves ordered cursor");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "future cursor part does not poison the gate before cursor catches up",
                Run = delegate
                {
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(2, 5, new EntityRecord[0]), "baseline");
                    SnapshotFreshnessGate gate = NewGate(2, 100, 5);
                    StateSnapshotMessage earlyPart = Wire(Snapshot(99, 101, 6, 0, 2, Entity(80, 1)));
                    if (RuntimeReplicaPolicy.CanPresentSnapshot(store, earlyPart))
                    {
                        Assert.True(gate.TryAcceptPart(earlyPart), "eligible part reaches gate");
                    }
                    Assert.Equal((ulong)0, gate.LatestSnapshotId, "blocked part did not advance freshness state");

                    WorldEventMessage event6 = Wire(Event(6, (byte)WorldEventSubType.WorldTime, 0, 0, new byte[] { 1 }));
                    EntityRecord eventRecord = RuntimeReplicaPolicy.CreateEventRecord(
                        store, event6, new EntitySnapshot { Kind = -1 }, false);
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(2, 6, eventRecord), "cursor advances");
                    StateSnapshotMessage retry = Wire(Snapshot(100, 102, 6, 0, 2, Entity(80, 1)));
                    Assert.True(RuntimeReplicaPolicy.CanPresentSnapshot(store, retry), "retry is now presentable");
                    Assert.True(gate.TryAcceptPart(retry), "new id part accepted");
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(100, 102, 6, 1, 2, Entity(81, 1)))), "same-id second part accepted");
                }
            });

            return s;
        }

        private static SnapshotFreshnessGate NewGate(uint epoch, ulong tick, ulong eventSeq)
        {
            SnapshotFreshnessGate gate = new SnapshotFreshnessGate();
            gate.BeginBaseline(epoch, tick, eventSeq);
            return gate;
        }

        private static ApplyResult ApplyOne(ReplicaStore store, StateSnapshotMessage msg)
        {
            EntitySnapshot snap = msg.Entities[0];
            return store.ApplySnapshot(store.Epoch, msg.IncludedEventSeq,
                new EntityRecord[] { Record(snap.EntityId, snap.Revision, snap.Kind) });
        }

        private static EntityRecord Record(ulong id, ulong revision, int kind)
        {
            return new EntityRecord(id, revision, kind, false);
        }

        private static WorldEventMessage Hit(ulong seq, ulong target, ulong revision)
        {
            HitResolvedPayload payload = new HitResolvedPayload();
            payload.AttackerEntityId = 1;
            payload.AttackSeq = seq;
            payload.TargetEntityId = target;
            payload.HitOrdinal = 0;
            payload.Outcome = IceyCoop.Core.HitOutcome.Damage;
            payload.HpAfter = 10;
            payload.ShieldAfter = 0;
            payload.Revision = revision;
            return Event(seq, (byte)WorldEventSubType.HitResolved, target, revision, HitResolvedCodec.Encode(payload));
        }

        private static WorldEventMessage Event(ulong seq, byte subtype, ulong target, ulong revision, byte[] payload)
        {
            WorldEventMessage message = new WorldEventMessage();
            message.EventSeq = seq;
            message.HostTick = seq + 100;
            message.SubType = subtype;
            message.TargetEntityId = target;
            message.EntityRevision = revision;
            message.Payload = payload ?? new byte[0];
            return message;
        }

        private static StateSnapshotMessage Snapshot(ulong id, ulong tick, ulong eventSeq,
            ushort partIndex, ushort partCount, EntitySnapshot entity)
        {
            StateSnapshotMessage message = new StateSnapshotMessage();
            message.SnapshotId = id;
            message.HostTick = tick;
            message.IncludedEventSeq = eventSeq;
            message.PartIndex = partIndex;
            message.PartCount = partCount;
            message.Entities = new EntitySnapshot[] { entity };
            return message;
        }

        private static EntitySnapshot Entity(ulong id, ulong revision)
        {
            EntitySnapshot snapshot = new EntitySnapshot();
            snapshot.EntityId = id;
            snapshot.Kind = 1;
            snapshot.Revision = revision;
            snapshot.LifeState = (byte)LifeState.Alive;
            snapshot.PrefabKey = "Dahal";
            return snapshot;
        }

        private static WorldEventMessage Wire(WorldEventMessage message)
        {
            return WorldEventMessage.Decode(message.Encode());
        }

        private static StateSnapshotMessage Wire(StateSnapshotMessage message)
        {
            return StateSnapshotMessage.Decode(message.Encode());
        }
    }
}
