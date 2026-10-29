using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class AnimKeySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "AnimKey";

            s.Tests.Add(new TestCase
            {
                Name = "hash is stable for the same name and zero for empty",
                Run = delegate
                {
                    uint a = AnimKey.Hash("Run");
                    uint b = AnimKey.Hash("Run");
                    Assert.Equal(a, b, "same name same hash");
                    Assert.True(a != AnimKey.None, "non empty name hashes to non zero");
                    Assert.Equal(AnimKey.None, AnimKey.Hash(string.Empty), "empty name");
                    Assert.Equal(AnimKey.None, AnimKey.Hash(null), "null name");
                    Assert.Equal(AnimKey.None, AnimKey.Hash("   "), "blank name");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hash distinguishes different names",
                Run = delegate
                {
                    Assert.True(AnimKey.Hash("Run") != AnimKey.Hash("Run2"), "Run vs Run2");
                    Assert.True(AnimKey.Hash("Atk1") != AnimKey.Hash("Atk2"), "Atk1 vs Atk2");
                    Assert.Equal(AnimKey.Hash("Run"), AnimKey.Hash("  Run  "), "trimmed before hashing");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hash matches a fixed reference value",
                Run = delegate
                {
                    // FNV-1a 32 (low then high UTF-16 byte per char) of "A" and
                    // "Run"; guards against accidental algorithm swaps that would
                    // desync host and client.
                    Assert.Equal(1792377636u, AnimKey.Hash("A"), "FNV-1a reference A");
                    Assert.Equal(1704228580u, AnimKey.Hash("Run"), "FNV-1a reference Run");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "normalize clamps into zero one and rejects bad durations",
                Run = delegate
                {
                    Assert.Equal(0.5f, AnimKey.Normalize(0.5f, 1f), "half");
                    Assert.Equal(0f, AnimKey.Normalize(-1f, 1f), "negative time");
                    Assert.Equal(1f, AnimKey.Normalize(2f, 1f), "overrun clamps");
                    Assert.Equal(0f, AnimKey.Normalize(1f, 0f), "zero duration");
                    Assert.Equal(0f, AnimKey.Normalize(1f, -2f), "negative duration");
                    Assert.Equal(0f, AnimKey.Normalize(float.NaN, 1f), "nan time");
                    Assert.Equal(0f, AnimKey.Normalize(1f, float.PositiveInfinity), "infinite duration");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "long names truncate without throwing",
                Run = delegate
                {
                    string longName = new string('x', 500);
                    uint h = AnimKey.Hash(longName);
                    Assert.Equal(h, AnimKey.Hash(new string('x', AnimKey.MaxNameLength)), "truncated at max name length");
                }
            });

            return s;
        }
    }
}
