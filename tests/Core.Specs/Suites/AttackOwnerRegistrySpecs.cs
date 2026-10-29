using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class AttackOwnerRegistrySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "AttackOwnerRegistry";

            s.Tests.Add(new TestCase
            {
                Name = "sequences allocated per attacker",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    Assert.Equal((ulong)1, reg.AllocateAttack(10), "a1");
                    Assert.Equal((ulong)2, reg.AllocateAttack(10), "a2");
                    Assert.Equal((ulong)1, reg.AllocateAttack(20), "b1");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "register and resolve owner",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    Assert.True(reg.RegisterAttack(10, 5), "register");
                    ulong owner;
                    Assert.True(reg.TryGetOwner(10, 5, out owner) && owner == 10, "owner");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "same key idempotent and different owner same seq coexists",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    Assert.True(reg.RegisterAttack(10, 5), "first");
                    Assert.True(reg.RegisterAttack(10, 5), "idempotent");
                    Assert.True(reg.RegisterAttack(20, 5), "different owner same seq no conflict");
                    ulong owner;
                    Assert.True(reg.TryGetOwner(10, 5, out owner) && owner == 10, "owner 10");
                    Assert.True(reg.TryGetOwner(20, 5, out owner) && owner == 20, "owner 20");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "release then re-register works",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    Assert.True(reg.RegisterAttack(10, 5), "first");
                    Assert.True(reg.ReleaseAttack(10, 5), "release");
                    Assert.True(reg.RegisterAttack(20, 5), "re-register different owner same seq");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "capacity trims oldest",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(2);
                    reg.RegisterAttack(10, 1);
                    reg.RegisterAttack(10, 2);
                    reg.RegisterAttack(10, 3);
                    ulong owner;
                    Assert.True(!reg.TryGetOwner(10, 1, out owner), "oldest evicted");
                    Assert.True(reg.TryGetOwner(10, 3, out owner), "newest kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "two owners first attack same seq coexist and resolve",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    Assert.Equal((ulong)1, reg.AllocateAttack(10), "p1 alloc");
                    Assert.Equal((ulong)1, reg.AllocateAttack(20), "p2 alloc");
                    Assert.True(reg.RegisterAttack(10, 1), "p1 register");
                    Assert.True(reg.RegisterAttack(20, 1), "p2 register same seq");
                    ulong owner;
                    Assert.True(reg.TryGetOwner(10, 1, out owner) && owner == 10, "p1 owner");
                    Assert.True(reg.TryGetOwner(20, 1, out owner) && owner == 20, "p2 owner");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "release only removes own entry",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    reg.RegisterAttack(10, 1);
                    reg.RegisterAttack(20, 1);
                    ulong owner;
                    Assert.True(!reg.ReleaseAttack(10, 2), "no such seq for owner");
                    Assert.True(reg.TryGetOwner(20, 1, out owner) && owner == 20, "other owner untouched");
                    Assert.True(reg.ReleaseAttack(10, 1), "release own");
                    Assert.True(!reg.TryGetOwner(10, 1, out owner), "own removed");
                    Assert.True(reg.TryGetOwner(20, 1, out owner) && owner == 20, "other owner still present");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "capacity trims oldest by registration order",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(2);
                    reg.RegisterAttack(10, 1);
                    reg.RegisterAttack(20, 1);
                    reg.RegisterAttack(10, 2);
                    ulong owner;
                    Assert.True(!reg.TryGetOwner(10, 1, out owner), "oldest evicted");
                    Assert.True(reg.TryGetOwner(20, 1, out owner) && owner == 20, "middle kept");
                    Assert.True(reg.TryGetOwner(10, 2, out owner) && owner == 10, "newest kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "same seq reusable after release",
                Run = delegate
                {
                    AttackOwnerRegistry reg = new AttackOwnerRegistry(64);
                    Assert.True(reg.RegisterAttack(10, 1), "first");
                    Assert.True(reg.ReleaseAttack(10, 1), "release");
                    Assert.True(reg.RegisterAttack(10, 1), "reuse same seq after release");
                    ulong owner;
                    Assert.True(reg.TryGetOwner(10, 1, out owner) && owner == 10, "resolved");
                }
            });

            return s;
        }
    }
}
