using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class SceneMigrationSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SceneMigration";

            s.Tests.Add(new TestCase
            {
                Name = "players survive a transition",
                Run = delegate
                {
                    Assert.Equal(MigrationAction.Keep, SceneMigration.Decide(MigrationKind.Player), "player");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "room-owned entities despawn",
                Run = delegate
                {
                    Assert.Equal(MigrationAction.Despawn, SceneMigration.Decide(MigrationKind.Enemy), "enemy");
                    Assert.Equal(MigrationAction.Despawn, SceneMigration.Decide(MigrationKind.Projectile), "projectile");
                    Assert.Equal(MigrationAction.Despawn, SceneMigration.Decide(MigrationKind.Interactable), "interactable");
                    Assert.Equal(MigrationAction.Despawn, SceneMigration.Decide(MigrationKind.Unknown), "unknown");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "rewards carry over so they cannot be re-earned",
                Run = delegate
                {
                    Assert.Equal(MigrationAction.CarryOver, SceneMigration.Decide(MigrationKind.Reward), "reward");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "table keeps order and counts per action",
                Run = delegate
                {
                    ulong[] ids = new ulong[] { 1, 2, 3, 4, 5 };
                    MigrationKind[] kinds = new MigrationKind[]
                    {
                        MigrationKind.Player, MigrationKind.Player, MigrationKind.Enemy,
                        MigrationKind.Reward, MigrationKind.Projectile
                    };
                    MigrationEntry[] table = SceneMigration.Build(ids, kinds);
                    Assert.Equal(5, table.Length, "length");
                    Assert.Equal((ulong)1, table[0].EntityId, "first id");
                    Assert.Equal((ulong)5, table[4].EntityId, "last id");
                    Assert.Equal(2, SceneMigration.Count(table, MigrationAction.Keep), "keep");
                    Assert.Equal(1, SceneMigration.Count(table, MigrationAction.CarryOver), "carry");
                    Assert.Equal(2, SceneMigration.Count(table, MigrationAction.Despawn), "despawn");
                    Assert.Equal("keep=2 carry=1 despawn=2", SceneMigration.Describe(table), "describe");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "mismatched input is rejected instead of guessed",
                Run = delegate
                {
                    bool threw = false;
                    try
                    {
                        SceneMigration.Build(new ulong[] { 1, 2 }, new MigrationKind[] { MigrationKind.Player });
                    }
                    catch (ArgumentException)
                    {
                        threw = true;
                    }
                    Assert.True(threw, "expected ArgumentException");
                }
            });

            return s;
        }
    }
}
