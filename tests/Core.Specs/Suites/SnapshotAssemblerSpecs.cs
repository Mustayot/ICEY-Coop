using System;
using System.Collections.Generic;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class SnapshotAssemblerSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SnapshotAssembler";

            s.Tests.Add(new TestCase
            {
                Name = "build is deterministic and sorted by entity id",
                Run = delegate
                {
                    EntitySource[] src = new EntitySource[]
                    {
                        Src(30, 1, 2, 3f, 4f),
                        Src(10, 1, 1, 1f, 2f),
                        Src(20, 2, 3, 5f, 6f)
                    };

                    SnapshotAssembler a1 = new SnapshotAssembler();
                    SnapshotAssembler a2 = new SnapshotAssembler();
                    List<EntitySnapshot> b1 = a1.Build(src);
                    List<EntitySnapshot> b2 = a2.Build(src);

                    AssertBytes(EncodeList(b1), EncodeList(b2), "same input same bytes");
                    Assert.Equal(3, b1.Count, "count");
                    Assert.Equal((ulong)10, b1[0].EntityId, "first sorted");
                    Assert.Equal((ulong)20, b1[1].EntityId, "second sorted");
                    Assert.Equal((ulong)30, b1[2].EntityId, "third sorted");
                    Assert.Equal((int)1, b1[0].Kind, "kind carried");
                    Assert.Equal((ulong)2, b1[2].Revision, "revision carried");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "slice keeps entity records whole within budget",
                Run = delegate
                {
                    // One record is 67 bytes on the wire (2 length + 65 body),
                    // so budget 140 fits exactly two records per part.
                    EntitySource[] src = new EntitySource[]
                    {
                        Src(3, 1, 1, 0f, 0f),
                        Src(1, 1, 1, 0f, 0f),
                        Src(5, 2, 2, 0f, 0f),
                        Src(2, 1, 1, 0f, 0f),
                        Src(4, 2, 2, 0f, 0f)
                    };

                    SnapshotAssembler asm = new SnapshotAssembler();
                    List<EntitySnapshot> built = asm.Build(src);
                    List<EntitySnapshot[]> parts = asm.Slice(built, 140);

                    Assert.Equal(3, parts.Count, "part count");
                    Assert.Equal(2, parts[0].Length, "part 0 size");
                    Assert.Equal(2, parts[1].Length, "part 1 size");
                    Assert.Equal(1, parts[2].Length, "part 2 size");
                    Assert.Equal((ulong)1, parts[0][0].EntityId, "part 0 first");
                    Assert.Equal((ulong)2, parts[0][1].EntityId, "part 0 second");
                    Assert.Equal((ulong)5, parts[2][0].EntityId, "last entity whole");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "slice reassembles to original snapshot",
                Run = delegate
                {
                    EntitySource[] src = new EntitySource[]
                    {
                        Src(9, 1, 9, -1f, -2f),
                        Src(2, 2, 2, 2f, 2f),
                        Src(5, 1, 5, 0.5f, 0.25f),
                        Src(7, 2, 7, 3f, 3f),
                        Src(1, 1, 1, 1f, 1f)
                    };

                    SnapshotAssembler asm = new SnapshotAssembler();
                    asm.IncludedEventSeq = 12;
                    asm.HostTick = 34;
                    List<EntitySnapshot> built = asm.Build(src);
                    List<EntitySnapshot[]> parts = asm.Slice(built, 140);

                    List<EntitySnapshot> rebuilt = new List<EntitySnapshot>();
                    for (int i = 0; i < parts.Count; i++)
                    {
                        StateSnapshotMessage m = new StateSnapshotMessage();
                        m.SnapshotId = 7;
                        m.HostTick = asm.HostTick;
                        m.IncludedEventSeq = asm.IncludedEventSeq;
                        m.PartIndex = (ushort)i;
                        m.PartCount = (ushort)parts.Count;
                        m.Entities = parts[i];

                        StateSnapshotMessage back = StateSnapshotMessage.Decode(m.Encode());
                        Assert.Equal((ushort)i, back.PartIndex, "part index " + i);
                        Assert.Equal((ushort)parts.Count, back.PartCount, "part count");
                        Assert.Equal((ulong)12, back.IncludedEventSeq, "included seq");
                        Assert.Equal((ulong)34, back.HostTick, "host tick");
                        for (int k = 0; k < back.Entities.Length; k++)
                        {
                            rebuilt.Add(back.Entities[k]);
                        }
                    }

                    Assert.Equal(built.Count, rebuilt.Count, "rebuilt count");
                    for (int i = 0; i < built.Count; i++)
                    {
                        Assert.Equal(built[i].EntityId, rebuilt[i].EntityId, "rebuilt id " + i);
                        Assert.Equal(built[i].Hp, rebuilt[i].Hp, "rebuilt hp " + i);
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "empty world yields one empty part",
                Run = delegate
                {
                    SnapshotAssembler asm = new SnapshotAssembler();
                    List<EntitySnapshot> built = asm.Build(new EntitySource[0]);
                    Assert.Equal(0, built.Count, "empty build");

                    List<EntitySnapshot[]> parts = asm.Slice(built, SnapshotAssembler.UdpSnapshotMaxBytes);
                    Assert.Equal(1, parts.Count, "one part");
                    Assert.Equal(0, parts[0].Length, "part empty");

                    StateSnapshotMessage m = new StateSnapshotMessage();
                    m.SnapshotId = 1;
                    m.HostTick = 1;
                    m.IncludedEventSeq = 0;
                    m.PartIndex = 0;
                    m.PartCount = 1;
                    m.Entities = parts[0];
                    StateSnapshotMessage back = StateSnapshotMessage.Decode(m.Encode());
                    Assert.Equal(0, back.Entities.Length, "empty snapshot round-trips");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "single record exceeding budget rejected",
                Run = delegate
                {
                    EntitySource[] src = new EntitySource[] { Src(1, 1, 1, 0f, 0f) };
                    SnapshotAssembler asm = new SnapshotAssembler();
                    List<EntitySnapshot> built = asm.Build(src);
                    Assert.Throws<ProtocolException>(delegate { asm.Slice(built, 60); }, "record larger than budget rejected");
                }
            });

            return s;
        }

        private static EntitySource Src(ulong id, int kind, ulong revision, float x, float y)
        {
            EntitySource s = new EntitySource();
            s.EntityId = id;
            s.Kind = kind;
            s.Revision = revision;
            s.X = x;
            s.Y = y;
            s.Vx = 0f;
            s.Vy = 0f;
            s.Facing = 1;
            s.AnimId = 10;
            s.AnimProgress = 0.5f;
            s.Hp = 100;
            s.Shield = 25;
            s.LifeState = (byte)LifeState.Alive;
            s.TargetEntityId = 0;
            return s;
        }

        private static byte[] EncodeList(List<EntitySnapshot> list)
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32((uint)list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                byte[] bytes = EntitySnapshotCodec.Encode(list[i]);
                w.WriteUInt16((ushort)bytes.Length);
                w.WriteBytes(bytes);
            }
            return w.ToArray();
        }

        private static void AssertBytes(byte[] expected, byte[] actual, string message)
        {
            if (expected == null || actual == null || expected.Length != actual.Length)
            {
                throw new InvalidOperationException(message + " (length mismatch)");
            }
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    throw new InvalidOperationException(message + " (byte " + i + ")");
                }
            }
        }
    }
}
