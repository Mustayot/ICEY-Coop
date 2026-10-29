using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class SnapshotFreshnessSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SnapshotFreshness";

            s.Tests.Add(new TestCase
            {
                Name = "newer snapshot is accepted and an older snapshot arriving later is rejected",
                Run = delegate
                {
                    SnapshotFreshnessGate gate = NewGate(4, 100, 20);
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(11, 102, 20, 0, 1))), "newer snapshot accepted");
                    Assert.True(!gate.TryAcceptPart(Wire(Snapshot(10, 101, 20, 0, 1))), "older snapshot rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "different parts from one split snapshot are both accepted",
                Run = delegate
                {
                    SnapshotFreshnessGate gate = NewGate(4, 100, 20);
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(7, 105, 20, 0, 2))), "part zero accepted");
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(7, 105, 20, 1, 2))), "part one accepted");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "duplicate part from one split snapshot is rejected",
                Run = delegate
                {
                    SnapshotFreshnessGate gate = NewGate(4, 100, 20);
                    StateSnapshotMessage part = Wire(Snapshot(7, 105, 20, 0, 2));
                    Assert.True(gate.TryAcceptPart(part), "first copy accepted");
                    Assert.True(!gate.TryAcceptPart(part), "duplicate copy rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "pre-baseline scene snapshot is rejected by host tick and event sequence barrier",
                Run = delegate
                {
                    SnapshotFreshnessGate byTick = NewGate(4, 100, 20);
                    Assert.True(!byTick.TryAcceptPart(Wire(Snapshot(30, 99, 20, 0, 1))), "old host tick rejected");

                    SnapshotFreshnessGate byEvent = NewGate(4, 100, 20);
                    Assert.True(!byEvent.TryAcceptPart(Wire(Snapshot(30, 100, 19, 0, 1))), "old included event cursor rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "new baseline resets snapshot id and event cursor for next room",
                Run = delegate
                {
                    SnapshotFreshnessGate gate = NewGate(4, 100, 20);
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(900, 150, 23, 0, 1))), "old room snapshot accepted before transition");

                    // HostTick and event sequence keep increasing across an in-session room transition.
                    gate.BeginBaseline(5, 151, 24);
                    Assert.True(!gate.TryAcceptPart(Wire(Snapshot(901, 150, 23, 0, 1))), "old room packet rejected after new room baseline");
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(901, 152, 24, 0, 1))), "new room snapshot after barrier accepted");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ReplicaStore prevents an older snapshot revision from replacing a newer reliable event",
                Run = delegate
                {
                    SnapshotFreshnessGate gate = NewGate(4, 100, 20);
                    ReplicaStore store = new ReplicaStore();
                    Assert.Equal(ApplyResult.Applied, store.ApplyBaseline(4, 20,
                        new EntityRecord[] { new EntityRecord(100, 1, 1, false) }), "baseline");
                    Assert.Equal(ApplyResult.Applied, store.ApplyEvent(4, 21,
                        new EntityRecord(100, 5, 1, false)), "new reliable event");
                    StateSnapshotMessage staleEntity = Snapshot(1, 101, 21, 0, 1);
                    staleEntity.Entities = new EntitySnapshot[] { Entity(100, 4) };
                    staleEntity = Wire(staleEntity);
                    Assert.True(gate.TryAcceptPart(staleEntity), "fresh metadata passes before per-entity merge");
                    Assert.Equal(ApplyResult.RevisionRollback, store.ApplySnapshot(4, staleEntity.IncludedEventSeq,
                        new EntityRecord[] { Record(staleEntity.Entities[0]) }), "older entity revision rejected");

                    EntityRecord current;
                    Assert.True(store.TryGetEntity(100, out current), "entity retained");
                    Assert.Equal((ulong)5, current.Revision, "reliable event revision remains current");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "session reset requires a fresh baseline before accepting snapshots",
                Run = delegate
                {
                    SnapshotFreshnessGate gate = NewGate(4, 100, 20);
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(5, 105, 20, 0, 1))), "active session snapshot accepted");
                    gate.Reset();
                    Assert.True(!gate.TryAcceptPart(Wire(Snapshot(6, 106, 20, 0, 1))), "no snapshot accepted without baseline");
                    gate.BeginBaseline(1, 0, 0);
                    Assert.True(gate.TryAcceptPart(Wire(Snapshot(1, 1, 0, 0, 1))), "new session starts with its own snapshot id");
                }
            });

            return s;
        }

        private static SnapshotFreshnessGate NewGate(uint epoch, ulong hostTick, ulong includedEventSeq)
        {
            SnapshotFreshnessGate gate = new SnapshotFreshnessGate();
            gate.BeginBaseline(epoch, hostTick, includedEventSeq);
            return gate;
        }

        private static StateSnapshotMessage Snapshot(ulong id, ulong hostTick, ulong includedEventSeq,
            ushort partIndex, ushort partCount)
        {
            StateSnapshotMessage message = new StateSnapshotMessage();
            message.SnapshotId = id;
            message.HostTick = hostTick;
            message.IncludedEventSeq = includedEventSeq;
            message.PartIndex = partIndex;
            message.PartCount = partCount;
            message.Entities = new EntitySnapshot[0];
            return message;
        }

        private static StateSnapshotMessage Wire(StateSnapshotMessage message)
        {
            return StateSnapshotMessage.Decode(message.Encode());
        }

        private static EntitySnapshot Entity(ulong entityId, ulong revision)
        {
            EntitySnapshot snapshot = new EntitySnapshot();
            snapshot.EntityId = entityId;
            snapshot.Kind = 1;
            snapshot.Revision = revision;
            snapshot.LifeState = (byte)LifeState.Alive;
            snapshot.PrefabKey = "Dahal";
            return snapshot;
        }

        private static EntityRecord Record(EntitySnapshot snapshot)
        {
            return new EntityRecord(snapshot.EntityId, snapshot.Revision, snapshot.Kind, false);
        }
    }
}
