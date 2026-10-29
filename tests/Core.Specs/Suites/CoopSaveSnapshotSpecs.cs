using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class CoopSaveSnapshotSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CoopSaveSnapshot";

            s.Tests.Add(new TestCase
            {
                Name = "round trip keeps world and both players",
                Run = delegate
                {
                    CoopSaveSnapshot snap = new CoopSaveSnapshot();
                    snap.SceneName = "C3L1S3";
                    snap.WorldEpoch = 7;
                    snap.P1Hp = 1200;
                    snap.P2Hp = 900;
                    snap.P1X = 1.5f;
                    snap.P1Y = -2.25f;
                    snap.P2X = 3.5f;
                    snap.P2Y = -2.25f;
                    snap.Content = "testbuild001";
            snap.SavedAtUnixMs = 1700000000000L;

                    string body = snap.Build();
                    Assert.True(CoopSaveSnapshot.IsValid(body), "checksum validates");

                    CoopSaveSnapshot back = CoopSaveSnapshot.Parse(body);
                    Assert.Equal("C3L1S3", back.SceneName, "scene");
                    Assert.Equal((uint)7, back.WorldEpoch, "epoch");
                    Assert.Equal(1200, back.P1Hp, "p1 hp");
                    Assert.Equal(900, back.P2Hp, "p2 hp");
                    Assert.Equal(3.5f, back.P2X, "p2 x");
                    Assert.Equal(1700000000000L, back.SavedAtUnixMs, "timestamp");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a truncated save is rejected instead of half loaded",
                Run = delegate
                {
                    CoopSaveSnapshot snap = new CoopSaveSnapshot();
                    snap.SceneName = "C1L2S4";
                    string body = snap.Build();
                    string cut = body.Substring(0, body.Length / 2);
                    Assert.True(!CoopSaveSnapshot.IsValid(cut), "half a file is not valid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "editing a value breaks the checksum",
                Run = delegate
                {
                    CoopSaveSnapshot snap = new CoopSaveSnapshot();
                    snap.SceneName = "C1L2S4";
                    snap.P1Hp = 100;
                    string body = snap.Build();
                    string tampered = body.Replace("p1=100", "p1=999");
                    Assert.True(!CoopSaveSnapshot.IsValid(tampered), "tampered file is not valid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unsupported save versions are rejected even with a valid checksum",
                Run = delegate
                {
                    CoopSaveSnapshot snap = ValidSnapshot();
                    // Derive the probe from the real format version instead of hard-coding the
                    // previous one: this test used to say "2" and silently became a no-op when the
                    // format moved to 3.
                    string newer = "version=" + (CoopSaveSnapshot.FormatVersion + 1) + "\n";
                    string older = "version=" + (CoopSaveSnapshot.FormatVersion - 1) + "\n";
                    string current = "version=" + CoopSaveSnapshot.FormatVersion + "\n";
                    string body = snap.Build();
                    Assert.True(!CoopSaveSnapshot.IsValid(Rechecksum(body.Replace(current, newer))), "a newer version must not be applied");
                    Assert.True(!CoopSaveSnapshot.IsValid(Rechecksum(body.Replace(current, older))), "an older version must not be applied");
                    Assert.True(CoopSaveSnapshot.IsValid(Rechecksum(body)), "the current version is still valid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "negative hit points are rejected",
                Run = delegate
                {
                    CoopSaveSnapshot snap = ValidSnapshot();
                    snap.P1Hp = -1;
                    Assert.True(!CoopSaveSnapshot.IsValid(snap.Build()), "HP cannot be negative");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "non-finite coordinates are rejected",
                Run = delegate
                {
                    CoopSaveSnapshot snap = ValidSnapshot();
                    snap.P2Y = float.PositiveInfinity;
                    Assert.True(!CoopSaveSnapshot.IsValid(snap.Build()), "infinite position cannot be restored");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "non-gameplay scenes are rejected",
                Run = delegate
                {
                    CoopSaveSnapshot snap = ValidSnapshot();
                    snap.SceneName = "ui_start";
                    Assert.True(!CoopSaveSnapshot.IsValid(snap.Build()), "menu scene cannot be restored as a gameplay save");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the build fingerprint round trips",
                Run = delegate
                {
                    CoopSaveSnapshot snap = ValidSnapshot();
                    snap.Content = "bb7d8fa751ee";
                    CoopSaveSnapshot back = CoopSaveSnapshot.Parse(snap.Build());
                    Assert.Equal("bb7d8fa751ee", back.Content, "fingerprint survives the file");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a save with an empty fingerprint is rejected even with a valid checksum",
                Run = delegate
                {
                    CoopSaveSnapshot snap = ValidSnapshot();
                    snap.Content = string.Empty;
                    Assert.True(!CoopSaveSnapshot.IsValid(snap.Build()), "no fingerprint means no restore");
                }
            });

            return s;
        }

        private static CoopSaveSnapshot ValidSnapshot()
        {
            CoopSaveSnapshot snap = new CoopSaveSnapshot();
            snap.SceneName = "C3L1S3";
            snap.WorldEpoch = 7;
            snap.P1Hp = 1200;
            snap.P2Hp = 900;
            snap.P1X = 1.5f;
            snap.P1Y = -2.25f;
            snap.P2X = 3.5f;
            snap.P2Y = -2.25f;
            snap.Content = "testbuild001";
            snap.SavedAtUnixMs = 1700000000000L;
            return snap;
        }

        private static string Rechecksum(string content)
        {
            int newline = content.IndexOf('\n');
            string body = content.Substring(newline + 1);
            return "checksum=" + CoopSaveSnapshot.ChecksumOf(body) + "\n" + body;
        }
    }
}
