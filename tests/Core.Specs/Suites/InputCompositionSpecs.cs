using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class InputCompositionSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "InputComposition";

            s.Tests.Add(new TestCase
            {
                Name = "the vanilla composite alone is enough",
                Run = delegate
                {
                    Assert.True(InputComposition.Skill(true, false, false, false), "vanilla button wins");
                    Assert.True(InputComposition.SkillAlt(true, false, false, false), "vanilla button wins (alt)");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the raw gamepad combo needs both keys",
                Run = delegate
                {
                    Assert.True(InputComposition.Skill(false, true, true, false), "L1 + square");
                    Assert.Equal(false, InputComposition.Skill(false, true, false, false), "L1 alone is not a skill");
                    Assert.Equal(false, InputComposition.Skill(false, false, true, false), "square alone is not a skill");
                    Assert.True(InputComposition.SkillAlt(false, true, true, false), "L1 + triangle");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the shipped default key covers a key map that lost the entry",
                Run = delegate
                {
                    Assert.True(InputComposition.Skill(false, false, false, true), "default key 8");
                    Assert.True(InputComposition.SkillAlt(false, false, false, true), "default key 9");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "no source and nothing pressed stays false",
                Run = delegate
                {
                    Assert.Equal(false, InputComposition.Skill(false, false, false, false), "idle");
                    Assert.Equal(false, InputComposition.SkillAlt(false, false, false, false), "idle (alt)");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "fallback flag mirrors the vanilla button",
                Run = delegate
                {
                    Assert.Equal(false, InputComposition.NeededFallback(true), "vanilla works");
                    Assert.Equal(true, InputComposition.NeededFallback(false), "fallback needed");
                }
            });

            return s;
        }
    }
}
