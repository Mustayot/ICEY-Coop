using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class PlayerContextSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "PlayerContext";

            s.Tests.Add(new TestCase
            {
                Name = "register two contexts and resolve",
                Run = delegate
                {
                    PlayerContextRegistry reg = new PlayerContextRegistry();
                    object a = new object();
                    object b = new object();
                    reg.Register(new PlayerContext { PlayerId = PlayerIds.P1, Root = a, Attribute = new object() });
                    reg.Register(new PlayerContext { PlayerId = PlayerIds.P2, Root = b, Attribute = new object() });

                    PlayerContext ctx;
                    Assert.True(reg.TryGet(PlayerIds.P1, out ctx), "p1 ctx");
                    Assert.Equal(PlayerIds.P1, ctx.PlayerId, "p1 id");
                    byte owner;
                    Assert.True(reg.TryResolveByObject(a, out owner) && owner == PlayerIds.P1, "root a owner");
                    Assert.True(reg.TryResolveByObject(b, out owner) && owner == PlayerIds.P2, "root b owner");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "duplicate player id rejected",
                Run = delegate
                {
                    PlayerContextRegistry reg = new PlayerContextRegistry();
                    reg.Register(new PlayerContext { PlayerId = PlayerIds.P1, Root = new object() });
                    Assert.True(!reg.Register(new PlayerContext { PlayerId = PlayerIds.P1, Root = new object() }), "dup");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid player id rejected",
                Run = delegate
                {
                    PlayerContextRegistry reg = new PlayerContextRegistry();
                    Assert.True(!reg.Register(new PlayerContext { PlayerId = 0 }), "invalid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "null object fields ignored",
                Run = delegate
                {
                    PlayerContextRegistry reg = new PlayerContextRegistry();
                    reg.Register(new PlayerContext { PlayerId = PlayerIds.P1, Root = new object() });
                    PlayerContext ctx;
                    reg.TryGet(PlayerIds.P1, out ctx);
                    byte owner;
                    Assert.True(!reg.TryResolveByObject(null, out owner), "null object");
                }
            });

            return s;
        }
    }
}
