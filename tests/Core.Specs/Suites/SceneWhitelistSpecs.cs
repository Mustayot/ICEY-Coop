using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class SceneWhitelistSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SceneWhitelist";

            s.Tests.Add(new TestCase
            {
                Name = "comma list trims blanks and keeps order",
                Run = delegate
                {
                    string[] items = SceneWhitelist.Parse(" C0 , ,C12,  ");
                    Assert.Equal(2, items.Length, "count");
                    Assert.Equal("C0", items[0], "first");
                    Assert.Equal("C12", items[1], "second");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "membership ignores case and rejects null",
                Run = delegate
                {
                    Assert.True(SceneWhitelist.IsListed("C0,C1", "c1"), "case insensitive");
                    Assert.True(!SceneWhitelist.IsListed("C0,C1", "C2"), "not listed");
                    Assert.True(!SceneWhitelist.IsListed("C0", null), "null scene");
                    Assert.True(!SceneWhitelist.IsListed(null, "C0"), "null list");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "strict mode refuses unlisted gameplay looking scene",
                Run = delegate
                {
                    Assert.True(!SceneWhitelist.IsAllowed("C0", true, "C1"), "C1 not listed under strict");
                    Assert.True(SceneWhitelist.IsAllowed("C0", true, "C0"), "listed scene passes strict");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "non strict keeps legacy heuristic but never menus",
                Run = delegate
                {
                    Assert.True(SceneWhitelist.IsAllowed("", false, "C03"), "legacy gameplay key");
                    Assert.True(!SceneWhitelist.IsAllowed("", false, "ui_start"), "menu rejected");
                    Assert.True(!SceneWhitelist.IsAllowed("", false, "splash"), "splash rejected");
                    Assert.True(!SceneWhitelist.IsAllowed("", false, ""), "empty rejected");
                    Assert.True(!SceneWhitelist.IsAllowed("", false, "C"), "too short rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "special scenes SHI and palace join but non-numeric palace is refused",
                Run = delegate
                {
                    Assert.True(SceneWhitelist.IsAllowed("", false, "SHI"), "SHI joins");
                    Assert.True(SceneWhitelist.IsAllowed("", false, "shi"), "SHI case insensitive");
                    Assert.True(SceneWhitelist.IsAllowed("", false, "palace0"), "palace0 joins");
                    Assert.True(SceneWhitelist.IsAllowed("", false, "palace7"), "palace7 joins");
                    Assert.True(!SceneWhitelist.IsAllowed("", false, "palace"), "bare palace refused");
                    Assert.True(!SceneWhitelist.IsAllowed("", false, "palaceX"), "non numeric palace refused");
                    Assert.True(!SceneWhitelist.IsAllowed("", true, "SHI"), "SHI still needs whitelist under strict");
                    Assert.True(!SceneWhitelist.IsAllowed("", true, "palace0"), "palace still needs whitelist under strict");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "describe names the deciding rule",
                Run = delegate
                {
                    Assert.Equal("whitelisted", SceneWhitelist.Describe("C0", true, "C0"), "listed");
                    Assert.True(SceneWhitelist.Describe("C0", true, "C07").IndexOf("strict") >= 0, "strict reason");
                    Assert.True(SceneWhitelist.Describe("", false, "C07").IndexOf("legacy") >= 0, "legacy reason");
                    Assert.Equal("not a gameplay scene", SceneWhitelist.Describe("", false, "ui_start"), "menu reason");
                }
            });

            return s;
        }
    }
}

