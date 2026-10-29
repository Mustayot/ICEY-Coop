using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class InputActionsSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "InputActions";

            s.Tests.Add(new TestCase
            {
                Name = "action ids unique and within max held",
                Run = delegate
                {
                    int[] ids = new int[]
                    {
                        InputActions.Attack, InputActions.Jump, InputActions.Dash,
                        InputActions.Skill, InputActions.SkillAlt, InputActions.Defence, InputActions.Up,
                        InputActions.Down, InputActions.CirtAttack, InputActions.UpRising, InputActions.HitGround,
                        InputActions.Charging, InputActions.Execute, InputActions.Interaction
                    };
                    for (int i = 0; i < ids.Length; i++)
                    {
                        Assert.True(ids[i] >= 0 && ids[i] < InputActions.MaxHeld, "id " + i + " in range");
                        for (int k = i + 1; k < ids.Length; k++)
                        {
                            Assert.True(ids[i] != ids[k], "unique " + i + "/" + k);
                        }
                    }
                    Assert.Equal(0, InputActions.Attack, "legacy attack id");
                    Assert.Equal(1, InputActions.Jump, "legacy jump id");
                    Assert.Equal(2, InputActions.Dash, "legacy dash id");
                    Assert.Equal(3, InputActions.Skill, "legacy skill id");
                    Assert.Equal(4, InputActions.Defence, "legacy defence id");
                    Assert.Equal(5, InputActions.Up, "legacy up id");
                    Assert.Equal(6, InputActions.Down, "legacy down id");
                    Assert.Equal(7, InputActions.SkillAlt, "legacy skill alt id");
                    Assert.Equal(8, InputActions.CirtAttack, "heavy id");
                    Assert.Equal(9, InputActions.UpRising, "up rising id");
                    Assert.Equal(10, InputActions.HitGround, "hit ground id");
                    Assert.Equal(11, InputActions.Charging, "charging id");
                    Assert.Equal(12, InputActions.Execute, "execute id");
                    Assert.Equal(13, InputActions.Interaction, "interaction id");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "held bits map one-to-one to action ids",
                Run = delegate
                {
                    ushort bits = (ushort)((1 << InputActions.Attack) | (1 << InputActions.Dash) |
                        (1 << InputActions.CirtAttack) | (1 << InputActions.UpRising) | (1 << InputActions.HitGround));
                    Assert.True((bits & (1 << InputActions.Attack)) != 0, "attack bit set");
                    Assert.True((bits & (1 << InputActions.Jump)) == 0, "jump bit clear");
                    Assert.True((bits & (1 << InputActions.Dash)) != 0, "dash bit set");
                    Assert.True((bits & (1 << InputActions.CirtAttack)) != 0, "heavy bit set");
                    Assert.True((bits & (1 << InputActions.UpRising)) != 0, "up rising bit set");
                    Assert.True((bits & (1 << InputActions.HitGround)) != 0, "hit ground bit set");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ordinal edge consumed exactly once",
                Run = delegate
                {
                    InputSnapshot snap = new InputSnapshot();
                    snap.RecordEdge(InputActions.Attack, true, (byte)0);
                    snap.RecordEdge(InputActions.Attack, true, (byte)0); // replayed packet
                    Assert.True(snap.ConsumePressed(InputActions.Attack), "first consume");
                    Assert.True(!snap.ConsumePressed(InputActions.Attack), "second consume blocked");

                    snap.ClearTransient();
                    snap.RecordEdge(InputActions.Attack, true, (byte)0);
                    Assert.True(snap.ConsumePressed(InputActions.Attack), "next frame consumes again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "action names match the trace vocabulary",
                Run = delegate
                {
                    Assert.Equal("Attack", InputActions.NameOf(InputActions.Attack), "attack");
                    Assert.Equal("Jump", InputActions.NameOf(InputActions.Jump), "jump");
                    Assert.Equal("Dash", InputActions.NameOf(InputActions.Dash), "dash");
                    Assert.Equal("Skill", InputActions.NameOf(InputActions.Skill), "skill");
                    Assert.Equal("SkillAlt", InputActions.NameOf(InputActions.SkillAlt), "skill alt");
                    Assert.Equal("Defence", InputActions.NameOf(InputActions.Defence), "defence");
                    Assert.Equal("Up", InputActions.NameOf(InputActions.Up), "up");
                    Assert.Equal("Down", InputActions.NameOf(InputActions.Down), "down");
                    Assert.Equal("CirtAttack", InputActions.NameOf(InputActions.CirtAttack), "heavy attack");
                    Assert.Equal("UpRising", InputActions.NameOf(InputActions.UpRising), "up rising");
                    Assert.Equal("HitGround", InputActions.NameOf(InputActions.HitGround), "hit ground");
                    Assert.Equal("Charging", InputActions.NameOf(InputActions.Charging), "charging");
                    Assert.Equal("Execute", InputActions.NameOf(InputActions.Execute), "execute");
                    Assert.Equal("Interaction", InputActions.NameOf(InputActions.Interaction), "interaction");
                    Assert.Equal("Action15", InputActions.NameOf(15), "unknown");
                }
            });

            return s;
        }
    }
}
