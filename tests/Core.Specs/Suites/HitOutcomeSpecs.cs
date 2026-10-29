using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class HitOutcomeSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "HitOutcome";

            s.Tests.Add(new TestCase
            {
                Name = "outcome table values are valid and distinct",
                Run = delegate
                {
                    byte[] all = new byte[]
                    {
                        HitOutcome.Damage, HitOutcome.Blocked, HitOutcome.Dodged, HitOutcome.Rejected
                    };
                    for (int i = 0; i < all.Length; i++)
                    {
                        Assert.True(HitOutcome.IsValid(all[i]), "value " + all[i] + " valid");
                        for (int k = i + 1; k < all.Length; k++)
                        {
                            Assert.True(all[i] != all[k], "distinct " + i + "/" + k);
                        }
                    }
                    Assert.True(!HitOutcome.IsValid(0), "zero invalid");
                    Assert.True(!HitOutcome.IsValid(5), "five invalid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "applied damage maps to damage",
                Run = delegate
                {
                    Assert.Equal(HitOutcome.Damage, HitOutcome.FromHurtCheck(true, false, false), "damage only");
                    Assert.Equal(HitOutcome.Damage, HitOutcome.FromHurtCheck(true, true, true), "damage wins over flags");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "defence success maps to blocked",
                Run = delegate
                {
                    Assert.Equal(HitOutcome.Blocked, HitOutcome.FromHurtCheck(false, true, false), "blocked");
                    Assert.Equal(HitOutcome.Blocked, HitOutcome.FromHurtCheck(false, true, true), "block precedence");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "side step maps to dodged and nothing maps to rejected",
                Run = delegate
                {
                    Assert.Equal(HitOutcome.Dodged, HitOutcome.FromHurtCheck(false, false, true), "dodged");
                    Assert.Equal(HitOutcome.Rejected, HitOutcome.FromHurtCheck(false, false, false), "armour or counter");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "names cover every valid value",
                Run = delegate
                {
                    Assert.Equal("Damage", HitOutcome.NameOf(HitOutcome.Damage), "damage name");
                    Assert.Equal("Blocked", HitOutcome.NameOf(HitOutcome.Blocked), "blocked name");
                    Assert.Equal("Dodged", HitOutcome.NameOf(HitOutcome.Dodged), "dodged name");
                    Assert.Equal("Rejected", HitOutcome.NameOf(HitOutcome.Rejected), "rejected name");
                    Assert.Equal("Unknown", HitOutcome.NameOf((byte)0), "unknown name");
                }
            });

            return s;
        }
    }
}
