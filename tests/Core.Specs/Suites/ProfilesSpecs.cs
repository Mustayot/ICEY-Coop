using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ProfilesSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Profiles";

            s.Tests.Add(new TestCase
            {
                Name = "deep copy does not alias enhancements",
                Run = delegate
                {
                    PlayerProfile a = new PlayerProfile();
                    a.RoleSlot = 1;
                    a.Hp = 100;
                    a.Enhancements.Add(new EnhancementEntry { Key = "maxHP", Level = 3 });

                    PlayerProfile b = a.DeepCopy();
                    b.Enhancements[0].Level = 9;
                    b.Hp = 50;

                    Assert.Equal(3, a.Enhancements[0].Level, "original entry unchanged");
                    Assert.Equal(100, a.Hp, "original hp unchanged");
                    Assert.Equal(9, b.Enhancements[0].Level, "copy changed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "two copies never share entries",
                Run = delegate
                {
                    PlayerProfile a = new PlayerProfile();
                    a.Enhancements.Add(new EnhancementEntry { Key = "atk", Level = 1 });
                    PlayerProfile b = a.DeepCopy();
                    PlayerProfile c = a.DeepCopy();
                    b.Enhancements.Add(new EnhancementEntry { Key = "extra", Level = 5 });
                    Assert.Equal(1, a.Enhancements.Count, "a count");
                    Assert.Equal(2, b.Enhancements.Count, "b count");
                    Assert.Equal(1, c.Enhancements.Count, "c count");
                }
            });

            return s;
        }
    }
}
