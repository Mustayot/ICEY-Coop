using System;
using System.Collections.Generic;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class CoopSaveV3Specs
    {
        private static CoopSaveSnapshot Sample()
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
            s.P1Profile["maxHP"] = 150;
            s.P1Profile["atkLevel"] = 3;
            s.P2Profile["maxHP"] = 140;
            s.P2Profile["atkLevel"] = 2;
            s.Encounters["C3L1S3:cp1"] = 4;
            s.Encounters["C3L1S3:cp2"] = 1;
            return s;
        }

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CoopSaveV3";

            s.Tests.Add(new TestCase
            {
                Name = "v3 carries money, both profiles and encounter checkpoints",
                Run = delegate
                {
                    CoopSaveSnapshot original = Sample();
                    string text = original.Build();
                    CoopSaveSnapshot parsed = CoopSaveSnapshot.Parse(text);
                    Assert.Equal(3, parsed.Version, "version 3");
                    Assert.Equal(3563, parsed.Money, "money round trips");
                    Assert.Equal(150, parsed.P1Profile["maxHP"], "p1 maxHP");
                    Assert.Equal(3, parsed.P1Profile["atkLevel"], "p1 atk");
                    Assert.Equal(140, parsed.P2Profile["maxHP"], "p2 maxHP");
                    Assert.Equal(2, parsed.P2Profile["atkLevel"], "p2 atk");
                    Assert.Equal((byte)4, parsed.Encounters["C3L1S3:cp1"], "encounter 1");
                    Assert.Equal((byte)1, parsed.Encounters["C3L1S3:cp2"], "encounter 2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an empty profile and empty encounter set are legal",
                Run = delegate
                {
                    CoopSaveSnapshot original = Sample();
                    original.P1Profile.Clear();
                    original.P2Profile.Clear();
                    original.Encounters.Clear();
                    CoopSaveSnapshot parsed = CoopSaveSnapshot.Parse(original.Build());
                    Assert.Equal(0, parsed.P1Profile.Count, "p1 profile empty");
                    Assert.Equal(0, parsed.P2Profile.Count, "p2 profile empty");
                    Assert.Equal(0, parsed.Encounters.Count, "encounters empty");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the text is deterministic regardless of insertion order",
                Run = delegate
                {
                    CoopSaveSnapshot a = Sample();
                    CoopSaveSnapshot b = new CoopSaveSnapshot();
                    b.SceneName = a.SceneName;
                    b.WorldEpoch = a.WorldEpoch;
                    b.Content = a.Content;
                    b.P1Hp = a.P1Hp;
                    b.P2Hp = a.P2Hp;
                    b.P1X = a.P1X; b.P1Y = a.P1Y; b.P2X = a.P2X; b.P2Y = a.P2Y;
                    b.SavedAtUnixMs = a.SavedAtUnixMs;
                    b.Money = a.Money;
                    // reversed insertion order
                    b.P1Profile["atkLevel"] = 3;
                    b.P1Profile["maxHP"] = 150;
                    b.P2Profile["atkLevel"] = 2;
                    b.P2Profile["maxHP"] = 140;
                    b.Encounters["C3L1S3:cp2"] = 1;
                    b.Encounters["C3L1S3:cp1"] = 4;
                    Assert.Equal(a.Build(), b.Build(), "same content, same bytes");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a v2 save is refused instead of being applied",
                Run = delegate
                {
                    CoopSaveSnapshot old = Sample();
                    string text = old.Build().Replace("version=3", "version=2");
                    // the checksum protected the body, so a hand edit must be rejected outright
                    Assert.True(!CoopSaveSnapshot.IsValid(text), "edited v2 body fails the checksum");
                    CoopSaveSnapshot forged = Sample();
                    forged.Version = 2;
                    string forgedText = forged.Build();
                    Assert.True(!CoopSaveSnapshot.IsValid(forgedText), "v2 is not a supported version");
                    bool refused = false;
                    try { CoopSaveSnapshot.Parse(forgedText); }
                    catch (InvalidOperationException) { refused = true; }
                    Assert.True(refused, "parse must refuse a v2 save");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "money is bounded",
                Run = delegate
                {
                    CoopSaveSnapshot negative = Sample();
                    negative.Money = -1;
                    Assert.True(!CoopSaveSnapshot.IsValid(negative.Build()), "negative money rejected");
                    CoopSaveSnapshot huge = Sample();
                    huge.Money = CoopSaveSnapshot.MaximumSavedMoney + 1;
                    Assert.True(!CoopSaveSnapshot.IsValid(huge.Build()), "money above the cap rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a profile field with an illegal name is rejected at encode time",
                Run = delegate
                {
                    SortedDictionary<string, int> values = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    values["bad name"] = 1;
                    string text;
                    string error;
                    Assert.True(!CoopProfileCodec.TryEncode(values, out text, out error), "space in name rejected");
                    Assert.True(error != null && error.IndexOf("illegal", StringComparison.OrdinalIgnoreCase) >= 0,
                        "reason names the problem: " + error);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a duplicate profile field is rejected at decode time",
                Run = delegate
                {
                    SortedDictionary<string, int> values;
                    string error;
                    Assert.True(!CoopProfileCodec.TryDecode("a:1;a:2", out values, out error), "duplicate rejected");
                    Assert.True(error != null && error.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0,
                        "reason names the duplicate: " + error);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "profile and encounter entry counts are capped",
                Run = delegate
                {
                    SortedDictionary<string, int> many = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i <= CoopProfileCodec.MaximumEntries; i++)
                    {
                        many["f" + i] = i;
                    }
                    string text;
                    string error;
                    Assert.True(!CoopProfileCodec.TryEncode(many, out text, out error), "too many profile fields");

                    SortedDictionary<string, byte> enc = new SortedDictionary<string, byte>(StringComparer.Ordinal);
                    for (int i = 0; i <= CoopEncounterCodec.MaximumEntries; i++)
                    {
                        enc["k" + i] = 1;
                    }
                    Assert.True(!CoopEncounterCodec.TryEncode(enc, out text, out error), "too many encounter keys");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an encounter state outside the known range is rejected",
                Run = delegate
                {
                    SortedDictionary<string, byte> states;
                    string error;
                    Assert.True(!CoopEncounterCodec.TryDecode("k:99", out states, out error), "state 99 rejected");
                    Assert.True(CoopEncounterCodec.TryDecode("k:4", out states, out error), "state 4 accepted");
                    Assert.Equal((byte)4, states["k"], "state kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a tampered body fails the checksum",
                Run = delegate
                {
                    string text = Sample().Build();
                    string tampered = text.Replace("money=3563", "money=9999");
                    Assert.True(!CoopSaveSnapshot.IsValid(tampered), "checksum catches the edit");
                }
            });

            return s;
        }
    }
}
