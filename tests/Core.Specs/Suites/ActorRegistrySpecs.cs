using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ActorRegistrySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ActorRegistry";

            s.Tests.Add(new TestCase
            {
                Name = "two roots resolve independently",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    object p1 = new object();
                    object p2 = new object();
                    Assert.Equal(RegistryError.None, reg.Register(p1, PlayerIds.P1), "register p1");
                    Assert.Equal(RegistryError.None, reg.Register(p2, PlayerIds.P2), "register p2");
                    byte owner;
                    Assert.True(reg.TryResolveOwner(p1, out owner) && owner == PlayerIds.P1, "p1 owner");
                    Assert.True(reg.TryResolveOwner(p2, out owner) && owner == PlayerIds.P2, "p2 owner");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "duplicate root rejected",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    object p1 = new object();
                    reg.Register(p1, PlayerIds.P1);
                    Assert.Equal(RegistryError.DuplicateRoot, reg.Register(p1, PlayerIds.P2), "duplicate");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "attached object inherits owner",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    object p1 = new object();
                    object ability = new object();
                    reg.Register(p1, PlayerIds.P1);
                    Assert.Equal(RegistryError.None, reg.Attach(ability, p1), "attach");
                    byte owner;
                    Assert.True(reg.TryResolveOwner(ability, out owner) && owner == PlayerIds.P1, "ability owner");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "same object cannot belong to two roots",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    object p1 = new object();
                    object p2 = new object();
                    object ability = new object();
                    reg.Register(p1, PlayerIds.P1);
                    reg.Register(p2, PlayerIds.P2);
                    reg.Attach(ability, p1);
                    Assert.Equal(RegistryError.DuplicateObject, reg.Attach(ability, p2), "cross attach");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unregister clears owned objects only",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    object p1 = new object();
                    object p2 = new object();
                    object a1 = new object();
                    object a2 = new object();
                    reg.Register(p1, PlayerIds.P1);
                    reg.Register(p2, PlayerIds.P2);
                    reg.Attach(a1, p1);
                    reg.Attach(a2, p2);
                    Assert.Equal(RegistryError.None, reg.Unregister(p1), "unregister p1");
                    byte owner;
                    Assert.True(!reg.TryResolveOwner(a1, out owner), "a1 cleared");
                    Assert.True(reg.TryResolveOwner(a2, out owner) && owner == PlayerIds.P2, "a2 kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "re-register after unregister works",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    object p1 = new object();
                    reg.Register(p1, PlayerIds.P1);
                    reg.Unregister(p1);
                    Assert.Equal(RegistryError.None, reg.Register(p1, PlayerIds.P1), "re-register");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unknown object and unknown root",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    byte owner;
                    Assert.True(!reg.TryResolveOwner(new object(), out owner), "unknown object");
                    Assert.Equal(RegistryError.UnknownRoot, reg.Unregister(new object()), "unknown root");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid player id rejected",
                Run = delegate
                {
                    ActorRegistry reg = new ActorRegistry();
                    Assert.Equal(RegistryError.InvalidPlayerId, reg.Register(new object(), 0), "invalid 0");
                    Assert.Equal(RegistryError.InvalidPlayerId, reg.Register(new object(), 3), "invalid 3");
                }
            });

            return s;
        }
    }
}
