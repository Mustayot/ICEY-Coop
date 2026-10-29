using System;
using System.Collections.Generic;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    /// <summary>
    /// T21 v3 read/write side (stage 2): the pure rules the save writer and restorer must obey
    /// so that money, both owner profiles and the encounter checkpoints survive a restart
    /// without "applying as far as it can be understood".
    /// </summary>
    public sealed class CoopSaveProgressSpecs
    {
        private static CoopSaveSnapshot Valid()
        {
            CoopSaveSnapshot s = new CoopSaveSnapshot();
            s.SceneName = "C3L1S3";
            s.WorldEpoch = 4;
            s.Content = "ABCDEF123456";
            s.P1Hp = 137;
            s.P2Hp = 77;
            s.P1X = 30.25f;
            s.P1Y = 2.38f;
            s.P2X = 33.5f;
            s.P2Y = 2.5f;
            s.SavedAtUnixMs = 1792771835170L;
            s.Money = 3563;
            s.P1Profile = CoopEnhancementFields.NewTable(0);
            s.P2Profile = CoopEnhancementFields.NewTable(0);
            s.P1Profile["maxHP"] = 1;
            s.P2Profile["combo1"] = 2;
            s.Encounters["C3L1S3:cp1"] = (byte)EncounterState.Cleared;
            s.Encounters["C3L1S3:cp2"] = (byte)EncounterState.Idle;
            return s;
        }

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CoopSaveProgress";

            s.Tests.Add(new TestCase
            {
                Name = "the enhancement vocabulary is exactly the native indexer's keys",
                Run = delegate
                {
                    Assert.Equal(20, CoopEnhancementFields.Names.Length, "twenty native fields");
                    // the native switch is case sensitive: "maxHP" and "maxEnergy" are the real keys
                    string[] expected = new string[]
                    {
                        "attack", "knockout", "avatarAttack", "airAttack", "airCombo2",
                        "airAvatarAttack", "airCombo1", "tripleAttack", "combo2", "upperChop",
                        "combo1", "bladeStorm", "shadeAttack", "charging", "chase",
                        "hitGround", "maxHP", "maxEnergy", "flashAttack", "recover"
                    };
                    for (int i = 0; i < expected.Length; i++)
                    {
                        Assert.Equal(expected[i], CoopEnhancementFields.Names[i], "field #" + i);
                        Assert.True(CoopEnhancementFields.IsKnown(expected[i]), "known: " + expected[i]);
                    }
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an unknown enhancement name is not mistaken for a native one",
                Run = delegate
                {
                    Assert.True(!CoopEnhancementFields.IsKnown("maxHp"), "wrong case rejected");
                    Assert.True(!CoopEnhancementFields.IsKnown("maxhealth"), "made-up name rejected");
                    Assert.True(!CoopEnhancementFields.IsKnown(null), "null rejected");
                    Assert.True(!CoopEnhancementFields.IsKnown(string.Empty), "empty rejected");
                    Assert.True(CoopEnhancementFields.IsKnown("maxEnergy"), "maxEnergy kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "enhancement levels are the native zero-to-three scale",
                Run = delegate
                {
                    SortedDictionary<string, int> values = CoopEnhancementFields.NewTable(0);
                    string error;
                    for (int level = 0; level <= CoopEnhancementFields.MaximumLevel; level++)
                    {
                        values["maxHP"] = level;
                        Assert.True(CoopEnhancementFields.TryValidate(values, out error),
                            "level " + level + " accepted: " + error);
                    }
                    values["maxHP"] = CoopEnhancementFields.MaximumLevel + 1;
                    Assert.True(!CoopEnhancementFields.TryValidate(values, out error), "level 4 rejected");
                    Assert.True(error != null && error.IndexOf("maxHP", StringComparison.Ordinal) >= 0,
                        "reason names the field: " + error);
                    values["maxHP"] = -1;
                    Assert.True(!CoopEnhancementFields.TryValidate(values, out error), "negative level rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an empty profile table carries every native field",
                Run = delegate
                {
                    SortedDictionary<string, int> table = CoopEnhancementFields.NewTable(1);
                    Assert.Equal(20, table.Count, "all native fields present");
                    for (int i = 0; i < CoopEnhancementFields.Names.Length; i++)
                    {
                        Assert.Equal(1, table[CoopEnhancementFields.Names[i]], "seeded value for " + CoopEnhancementFields.Names[i]);
                    }
                    string error;
                    Assert.True(CoopEnhancementFields.TryValidate(table, out error), "a seeded table is valid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a cleared encounter stays cleared when it is restored",
                Run = delegate
                {
                    EncounterDirector d = new EncounterDirector("C3L1S3:cp1", true);
                    d.RequestTrigger(out _);
                    d.BeginSpawning(2);
                    d.MarkActive();
                    d.ObserveEnemyDeath();
                    d.ObserveEnemyDeath();
                    Assert.Equal(EncounterState.Cleared, d.State, "natively cleared");
                    string error;
                    Assert.True(d.TryRestoreSavedState((byte)EncounterState.Cleared, out error), "restored: " + error);
                    Assert.Equal(EncounterState.Cleared, d.State, "still cleared");
                    Assert.Equal(0, d.AliveEnemies, "no phantom enemies");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a wave that was still running comes back as idle, never as active",
                Run = delegate
                {
                    // The wave's enemies do not survive the restart, so a restored Active state
                    // would wait for deaths that can never arrive and stay unclearable forever.
                    EncounterDirector d = new EncounterDirector("C3L1S3:cp2", true);
                    d.RequestTrigger(out _);
                    d.BeginSpawning(3);
                    d.MarkActive();
                    Assert.Equal(EncounterState.Active, d.State, "native active");
                    string error;
                    Assert.True(d.TryRestoreSavedState((byte)EncounterState.Active, out error), "restored: " + error);
                    Assert.Equal(EncounterState.Idle, d.State, "demoted to idle");
                    Assert.Equal(0, d.AliveEnemies, "alive counter cleared");

                    uint generation;
                    Assert.True(d.RequestTrigger(out generation), "the checkpoint can start its wave again");
                    Assert.True(generation > 0, "a fresh generation");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a saved encounter state outside the native range is refused untouched",
                Run = delegate
                {
                    EncounterDirector d = new EncounterDirector("C3L1S3:cp3", true);
                    d.RequestTrigger(out _);
                    d.BeginSpawning(1);
                    d.MarkActive();
                    string error;
                    Assert.True(!d.TryRestoreSavedState(CoopEncounterCodec.MaximumStateValue, out error),
                        "state 15 refused");
                    Assert.True(error != null && error.Length > 0, "reason present");
                    Assert.Equal(EncounterState.Active, d.State, "state left alone");
                    Assert.Equal(1, d.AliveEnemies, "alive counter left alone");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a snapshot with native profiles and encounter states validates",
                Run = delegate
                {
                    CoopSaveSnapshot snap = Valid();
                    string error;
                    Assert.True(snap.TryValidate(out error), "base snapshot valid: " + error);
                    Assert.True(snap.TryValidateNativeProgress(out error), "native progress valid: " + error);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an unknown profile field is refused before anything is written",
                Run = delegate
                {
                    CoopSaveSnapshot snap = Valid();
                    snap.P2Profile["bogusField"] = 1;
                    string error;
                    Assert.True(snap.TryValidate(out error), "the storage layer still round trips it");
                    Assert.True(!snap.TryValidateNativeProgress(out error), "the restorer refuses it");
                    Assert.True(error != null && error.IndexOf("bogusField", StringComparison.Ordinal) >= 0,
                        "reason names the field: " + error);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an enhancement level above the native maximum is refused",
                Run = delegate
                {
                    CoopSaveSnapshot snap = Valid();
                    snap.P1Profile["maxHP"] = CoopEnhancementFields.MaximumLevel + 1;
                    string error;
                    Assert.True(!snap.TryValidateNativeProgress(out error), "level 4 refused");
                    Assert.True(error != null && error.IndexOf("maxHP", StringComparison.Ordinal) >= 0,
                        "reason names the field: " + error);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an encounter state the codec accepts but the director cannot is refused",
                Run = delegate
                {
                    CoopSaveSnapshot snap = Valid();
                    snap.Encounters["C3L1S3:cp9"] = CoopEncounterCodec.MaximumStateValue;
                    string error;
                    Assert.True(CoopSaveSnapshot.IsValid(snap.Build()), "the storage layer accepts 15");
                    Assert.True(!snap.TryValidateNativeProgress(out error), "the restorer refuses 15");
                    Assert.True(error != null && error.IndexOf("C3L1S3:cp9", StringComparison.Ordinal) >= 0,
                        "reason names the key: " + error);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a partial profile is legal and leaves the other fields alone",
                Run = delegate
                {
                    CoopSaveSnapshot snap = Valid();
                    snap.P1Profile.Clear();
                    snap.P1Profile["combo1"] = 3;
                    string error;
                    Assert.True(snap.TryValidateNativeProgress(out error), "partial profile valid: " + error);
                }
            });

            return s;
        }
    }
}
