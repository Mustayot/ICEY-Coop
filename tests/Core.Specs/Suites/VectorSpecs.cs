using System;
using System.IO;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    /// <summary>
    /// Writes canonical protocol bytes for three fixed messages so the in-game
    /// ProtocolVectorProbe can encode the same inputs and compare byte-for-byte.
    /// File layout: uint32 frameCount, then per frame uint32 length + bytes.
    /// </summary>
    public sealed class VectorSpecs
    {
        private static readonly string VectorPath = Path.Combine(Environment.CurrentDirectory, "artifacts/native/protocol-vectors.bin");

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Vector";

            s.Tests.Add(new TestCase
            {
                Name = "protocol vectors written to artifacts",
                Run = delegate
                {
                    WriteVectors(VectorPath);
                    Assert.True(File.Exists(VectorPath), "vectors file exists");
                    Assert.True(new FileInfo(VectorPath).Length > 0, "vectors file non-empty");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "protocol vectors read back and decode",
                Run = delegate
                {
                    WriteVectors(VectorPath);
                    byte[] all = File.ReadAllBytes(VectorPath);

                    ByteReader r = new ByteReader(all);
                    uint count = r.ReadUInt32();
                    Assert.Equal((uint)3, count, "frame count");

                    uint len1 = r.ReadUInt32();
                    HelloMessage hello = HelloMessage.Decode(r.ReadBytes((int)len1));
                    Assert.Equal((ushort)1, hello.Protocol, "hello protocol");
                    Assert.Equal("iceycoop", hello.Mod, "hello mod");
                    Assert.Equal("g3-vector", hello.Build, "hello build");
                    Assert.Equal((byte)2, hello.RequestedSlot, "hello slot");
                    Assert.Equal((uint)3, hello.Capabilities, "hello capabilities");

                    uint len2 = r.ReadUInt32();
                    WorldEventMessage world = WorldEventMessage.Decode(r.ReadBytes((int)len2));
                    Assert.Equal((ulong)1, world.EventSeq, "world event seq");
                    Assert.Equal((byte)WorldEventSubType.EntitySpawn, world.SubType, "world subtype");
                    Assert.Equal((ulong)1001, world.TargetEntityId, "world target");
                    EntitySnapshot spawned = EntitySnapshotCodec.Decode(world.Payload);
                    Assert.Equal((ulong)1001, spawned.EntityId, "spawn entity id");
                    Assert.Equal((int)100, spawned.Hp, "spawn hp");

                    uint len3 = r.ReadUInt32();
                    StateSnapshotMessage snap = StateSnapshotMessage.Decode(r.ReadBytes((int)len3));
                    Assert.Equal((ulong)42, snap.SnapshotId, "snapshot id");
                    Assert.Equal(2, snap.Entities.Length, "snapshot entity count");
                    Assert.Equal((ulong)1001, snap.Entities[0].EntityId, "snapshot entity 1");
                    Assert.Equal((ulong)2002, snap.Entities[1].EntityId, "snapshot entity 2");
                    Assert.Equal(0, r.Remaining, "no trailing bytes");
                }
            });

            return s;
        }

        private static void WriteVectors(string path)
        {
            byte[] hello = FixedHello().Encode();
            byte[] world = FixedWorld().Encode();
            byte[] snap = FixedSnapshot().Encode();

            ByteWriter w = new ByteWriter();
            w.WriteUInt32(3);
            w.WriteUInt32((uint)hello.Length);
            w.WriteBytes(hello);
            w.WriteUInt32((uint)world.Length);
            w.WriteBytes(world);
            w.WriteUInt32((uint)snap.Length);
            w.WriteBytes(snap);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllBytes(path, w.ToArray());
        }

        private static HelloMessage FixedHello()
        {
            HelloMessage m = new HelloMessage();
            m.Protocol = 1;
            m.Mod = "iceycoop";
            m.Build = "g3-vector";
            m.ContentDigest = B16(0x30);
            m.RequestedSlot = 2;
            m.ReconnectTicket = new byte[] { 0xAA, 0xBB };
            m.Capabilities = 3;
            return m;
        }

        private static WorldEventMessage FixedWorld()
        {
            EntitySnapshot s = new EntitySnapshot();
            s.EntityId = 1001;
            s.Kind = 1;
            s.Revision = 1;
            s.X = 0.5f;
            s.Y = 1.5f;
            s.Vx = 0f;
            s.Vy = 0f;
            s.Facing = 1;
            s.AnimId = 10;
            s.AnimProgress = 0.25f;
            s.Hp = 100;
            s.Shield = 50;
            s.LifeState = (byte)LifeState.Alive;
            s.TargetEntityId = 0;

            WorldEventMessage m = new WorldEventMessage();
            m.EventSeq = 1;
            m.HostTick = 100;
            m.SubType = (byte)WorldEventSubType.EntitySpawn;
            m.TargetEntityId = 1001;
            m.EntityRevision = 1;
            m.Payload = EntitySnapshotCodec.Encode(s);
            return m;
        }

        private static StateSnapshotMessage FixedSnapshot()
        {
            EntitySnapshot a = new EntitySnapshot();
            a.EntityId = 1001;
            a.Kind = 1;
            a.Revision = 1;
            a.X = 0.5f;
            a.Y = 1.5f;
            a.Vx = 0f;
            a.Vy = 0f;
            a.Facing = 1;
            a.AnimId = 10;
            a.AnimProgress = 0.25f;
            a.Hp = 100;
            a.Shield = 50;
            a.LifeState = (byte)LifeState.Alive;
            a.TargetEntityId = 0;

            EntitySnapshot b = new EntitySnapshot();
            b.EntityId = 2002;
            b.Kind = 2;
            b.Revision = 5;
            b.X = -3f;
            b.Y = 2f;
            b.Vx = 0.5f;
            b.Vy = 0f;
            b.Facing = -1;
            b.AnimId = 20;
            b.AnimProgress = 0.75f;
            b.Hp = 60;
            b.Shield = 0;
            b.LifeState = (byte)LifeState.Alive;
            b.TargetEntityId = 1001;

            StateSnapshotMessage m = new StateSnapshotMessage();
            m.SnapshotId = 42;
            m.HostTick = 100;
            m.IncludedEventSeq = 1;
            m.PartIndex = 0;
            m.PartCount = 1;
            m.Entities = new EntitySnapshot[] { a, b };
            return m;
        }

        private static byte[] B16(byte fill)
        {
            byte[] b = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                b[i] = (byte)(fill + i);
            }
            return b;
        }
    }
}
