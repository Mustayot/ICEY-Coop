using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class RecoveryOwnerSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "RecoveryOwner";

            s.Tests.Add(new TestCase
            {
                Name = "a fresh arbiter is free",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    Assert.Equal(RecoveryOwner.None, arbiter.Current, "nobody owns recovery");
                    Assert.True(arbiter.IsFree, "free");
                    Assert.True(!arbiter.IsHeldByAnother(RecoveryOwner.PartnerRevive), "not blocked");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "only one recovery transaction may hold the world",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    Assert.True(arbiter.TryClaim(RecoveryOwner.PartnerRevive), "first claim wins");
                    Assert.True(!arbiter.TryClaim(RecoveryOwner.WipeRestore), "second claim refused");
                    Assert.Equal(RecoveryOwner.PartnerRevive, arbiter.Current, "first owner kept");
                    Assert.Equal(RecoveryOwner.PartnerRevive, arbiter.BlockedBy, "diagnostic names the holder");
                    Assert.Equal(1, arbiter.Refusals, "one refusal");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the holder may claim again without deadlocking itself",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    Assert.True(arbiter.TryClaim(RecoveryOwner.WipeRestore), "claimed");
                    Assert.True(arbiter.TryClaim(RecoveryOwner.WipeRestore), "re-claim by the holder is fine");
                    Assert.Equal(1, arbiter.Claims, "counted once");
                    Assert.Equal(0, arbiter.Refusals, "no refusal");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "only the holder can release",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    arbiter.TryClaim(RecoveryOwner.StartupResume);
                    Assert.True(!arbiter.Release(RecoveryOwner.PartnerRevive), "a stranger cannot release");
                    Assert.Equal(RecoveryOwner.StartupResume, arbiter.Current, "ownership unchanged");
                    Assert.True(arbiter.Release(RecoveryOwner.StartupResume), "holder releases");
                    Assert.True(arbiter.IsFree, "free again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ownership is handed over only after a release",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    arbiter.TryClaim(RecoveryOwner.PartnerRevive);
                    Assert.True(!arbiter.TryClaim(RecoveryOwner.WipeRestore), "blocked while partner revive runs");
                    arbiter.Release(RecoveryOwner.PartnerRevive);
                    Assert.True(arbiter.TryClaim(RecoveryOwner.WipeRestore), "wipe may take over");
                    Assert.Equal(RecoveryOwner.WipeRestore, arbiter.Current, "handed over");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "claiming none is a programming error",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    Assert.Throws<ArgumentOutOfRangeException>(
                        delegate { arbiter.TryClaim(RecoveryOwner.None); }, "None cannot be claimed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reset frees the world at a session boundary",
                Run = delegate
                {
                    RecoveryArbiter arbiter = new RecoveryArbiter();
                    arbiter.TryClaim(RecoveryOwner.PartnerRevive);
                    arbiter.TryClaim(RecoveryOwner.WipeRestore);
                    arbiter.Reset();
                    Assert.Equal(RecoveryOwner.None, arbiter.Current, "free");
                    Assert.Equal(0, arbiter.Claims, "counters cleared");
                    Assert.Equal(0, arbiter.Refusals, "counters cleared");
                    Assert.Equal(RecoveryOwner.None, arbiter.BlockedBy, "diagnostic cleared");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the shared default arbiter is a single instance",
                Run = delegate
                {
                    Assert.True(ReferenceEquals(RecoveryArbiter.Default, RecoveryArbiter.Default),
                        "bridge code must all see the same arbiter");
                    RecoveryOwner before = RecoveryArbiter.Default.Current;
                    try
                    {
                        RecoveryArbiter.Default.Reset();
                        Assert.True(RecoveryArbiter.Default.IsFree, "default is free after reset");
                    }
                    finally
                    {
                        // Leave the shared instance as it was so other specs are order independent.
                        RecoveryArbiter.Default.Reset();
                        if (before != RecoveryOwner.None)
                        {
                            RecoveryArbiter.Default.TryClaim(before);
                        }
                    }
                }
            });

            return s;
        }
    }
}
