using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class IdentitySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Identity";

            s.Tests.Add(new TestCase
            {
                Name = "SessionId requires 16 bytes",
                Run = delegate
                {
                    Assert.Throws<ArgumentException>(delegate { new SessionId(new byte[15]); }, "15 bytes must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "SessionId round-trips 16 bytes",
                Run = delegate
                {
                    byte[] raw = new byte[16];
                    raw[0] = 1;
                    raw[15] = 255;
                    SessionId id = new SessionId(raw);
                    Assert.Equal(raw[0], id.ToArray()[0], "first byte");
                    Assert.Equal(raw[15], id.ToArray()[15], "last byte");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "SessionId.Zero is zero",
                Run = delegate
                {
                    Assert.True(SessionId.Zero.IsZero(), "Zero must be zero");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "PlayerIds only 1 or 2",
                Run = delegate
                {
                    Assert.True(PlayerIds.IsValid(PlayerIds.P1), "1 valid");
                    Assert.True(PlayerIds.IsValid(PlayerIds.P2), "2 valid");
                    Assert.True(!PlayerIds.IsValid(0), "0 invalid");
                    Assert.True(!PlayerIds.IsValid(3), "3 invalid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "HitKey distinguishes ordinal and equals itself",
                Run = delegate
                {
                    HitKey a = new HitKey(1, 10, 5, 20, 1);
                    HitKey b = new HitKey(1, 10, 5, 20, 2);
                    Assert.True(!a.Equals(b), "different ordinal must differ");
                    Assert.Equal(a, a, "same key equal");
                }
            });

            return s;
        }
    }
}
