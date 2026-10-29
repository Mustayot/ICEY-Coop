using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class EntitySnapshotSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "EntitySnapshot";

            s.Tests.Add(new TestCase
            {
                Name = "prefab key survives the round trip",
                Run = delegate
                {
                    EntitySnapshot snap = new EntitySnapshot();
                    snap.EntityId = 42;
                    snap.Kind = 1;
                    snap.Revision = 7;
                    snap.Hp = 300;
                    snap.Shield = 10;
                    snap.LifeState = 1;
                    snap.AnimId = 1792377636u;
                    snap.AnimProgress = 0.25f;
                    snap.PrefabKey = "EnemyDao";

                    EntitySnapshot back = EntitySnapshotCodec.Decode(EntitySnapshotCodec.Encode(snap));
                    Assert.Equal(42UL, back.EntityId, "entity id");
                    Assert.Equal("EnemyDao", back.PrefabKey, "prefab key");
                    Assert.Equal(1792377636u, back.AnimId, "anim id");
                    Assert.Equal(0.25f, back.AnimProgress, "anim progress");
                    Assert.Equal(300, back.Hp, "hp");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "missing prefab key encodes as empty string",
                Run = delegate
                {
                    EntitySnapshot snap = new EntitySnapshot();
                    snap.EntityId = 1;
                    snap.Kind = 2;
                    snap.LifeState = 1;
                    snap.PrefabKey = null;
                    EntitySnapshot back = EntitySnapshotCodec.Decode(EntitySnapshotCodec.Encode(snap));
                    Assert.Equal(string.Empty, back.PrefabKey, "null key becomes empty");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "oversized prefab key is rejected",
                Run = delegate
                {
                    EntitySnapshot snap = new EntitySnapshot();
                    snap.EntityId = 1;
                    snap.Kind = 1;
                    snap.LifeState = 1;
                    snap.PrefabKey = new string('k', ProtocolConstants.MaxStringBytes + 1);
                    Assert.Throws<ProtocolException>(delegate
                    {
                        EntitySnapshotCodec.Encode(snap);
                    }, "oversized prefab key must not be encoded");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "truncated prefab key bytes are rejected",
                Run = delegate
                {
                    EntitySnapshot snap = new EntitySnapshot();
                    snap.EntityId = 5;
                    snap.Kind = 1;
                    snap.LifeState = 1;
                    snap.PrefabKey = "EnemyBee";
                    byte[] full = EntitySnapshotCodec.Encode(snap);
                    byte[] cut = new byte[full.Length - 3];
                    Array.Copy(full, cut, cut.Length);
                    Assert.Throws<ProtocolException>(delegate
                    {
                        EntitySnapshotCodec.Decode(cut);
                    }, "truncated snapshot must be rejected");
                }
            });

            return s;
        }
    }
}
