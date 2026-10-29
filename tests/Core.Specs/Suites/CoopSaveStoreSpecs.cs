using System;
using System.IO;
using System.Text.Json;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class CoopSaveStoreSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CoopSaveStore";

            s.Tests.Add(new TestCase
            {
                Name = "first save writes, second save keeps backup",
                Run = delegate
                {
                    string dir = TempDir();
                    string path = Path.Combine(dir, "coop.json");
                    CoopSaveStore store = new CoopSaveStore();
                    store.SaveAtomic(path, "v1");
                    Assert.True(File.Exists(path), "main exists");
                    Assert.True(!File.Exists(path + ".bak"), "no backup after first save");

                    store.SaveAtomic(path, "v2");
                    Assert.Equal("v2", File.ReadAllText(path), "main is v2");
                    Assert.Equal("v1", File.ReadAllText(path + ".bak"), "backup is v1");
                    Directory.Delete(dir, true);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "load latest returns valid main",
                Run = delegate
                {
                    string dir = TempDir();
                    string path = Path.Combine(dir, "coop.json");
                    CoopSaveStore store = new CoopSaveStore();
                    store.SaveAtomic(path, "{\"v\":2}");
                    string loaded = store.LoadLatest(path, IsJson);
                    Assert.Equal("{\"v\":2}", loaded, "loaded");
                    Directory.Delete(dir, true);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "corrupt main falls back to backup",
                Run = delegate
                {
                    string dir = TempDir();
                    string path = Path.Combine(dir, "coop.json");
                    CoopSaveStore store = new CoopSaveStore();
                    store.SaveAtomic(path, "{\"v\":1}");
                    store.SaveAtomic(path, "{\"v\":2}");
                    File.WriteAllText(path, "{torn"); // simulate torn write
                    string loaded = store.LoadLatest(path, IsJson);
                    Assert.Equal("{\"v\":1}", loaded, "backup recovered");
                    Directory.Delete(dir, true);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "missing everything returns null",
                Run = delegate
                {
                    string dir = TempDir();
                    string path = Path.Combine(dir, "nope.json");
                    CoopSaveStore store = new CoopSaveStore();
                    Assert.True(store.LoadLatest(path, IsJson) == null, "null");
                    Directory.Delete(dir, true);
                }
            });

            return s;
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "iceycoop-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static bool IsJson(string content)
        {
            if (content == null)
            {
                return false;
            }
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(content))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
