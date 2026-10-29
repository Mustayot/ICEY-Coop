using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class SaveCompatibilitySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SaveCompatibility";

            s.Tests.Add(new TestCase
            {
                Name = "the same build may apply its own save",
                Run = delegate
                {
                    string reason;
                    Assert.Equal(SaveVerdict.Compatible,
                        SaveCompatibility.Decide("abc123def456", "abc123def456", false, out reason), "state");
                    Assert.True(reason.IndexOf("same build", StringComparison.Ordinal) >= 0, "reason");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a save from another build is refused with both ids in the reason",
                Run = delegate
                {
                    string reason;
                    SaveVerdict state = SaveCompatibility.Decide("aaaaaaaaaaaa", "bbbbbbbbbbbb", false, out reason);
                    Assert.Equal(SaveVerdict.Mismatch, state, "state");
                    Assert.True(reason.IndexOf("aaaaaaaaaaaa", StringComparison.Ordinal) >= 0, "names the saved build");
                    Assert.True(reason.IndexOf("bbbbbbbbbbbb", StringComparison.Ordinal) >= 0, "names the running build");
                    Assert.True(!SaveCompatibility.IsAllowed("aaaaaaaaaaaa", "bbbbbbbbbbbb", false, out reason), "not allowed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a save without a fingerprint is not trusted",
                Run = delegate
                {
                    string reason;
                    Assert.Equal(SaveVerdict.MissingFingerprint,
                        SaveCompatibility.Decide(null, "abc123", false, out reason), "null saved");
                    Assert.Equal(SaveVerdict.MissingFingerprint,
                        SaveCompatibility.Decide("   ", "abc123", false, out reason), "blank saved");
                    Assert.Equal(SaveVerdict.MissingFingerprint,
                        SaveCompatibility.Decide("abc123", null, false, out reason), "this build has no id");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the operator override lets a mixed build through and says so",
                Run = delegate
                {
                    string reason;
                    Assert.Equal(SaveVerdict.Compatible,
                        SaveCompatibility.Decide("aaaaaaaaaaaa", "bbbbbbbbbbbb", true, out reason), "overridden");
                    Assert.True(reason.IndexOf(SaveCompatibility.OverrideEnv, StringComparison.Ordinal) >= 0, "names the switch");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "surrounding whitespace does not fake a match or a mismatch",
                Run = delegate
                {
                    string reason;
                    Assert.Equal(SaveVerdict.Compatible,
                        SaveCompatibility.Decide(" abc123 \n", "abc123", false, out reason), "trimmed match");
                }
            });

            return s;
        }
    }
}
