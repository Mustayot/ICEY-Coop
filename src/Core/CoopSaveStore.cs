using System;
using System.IO;
using System.Text;

namespace IceyCoop.Core
{
    /// <summary>
    /// Atomic save store: same-directory temp file + File.Replace with one valid
    /// backup. LoadLatest validates the main file and falls back to the backup,
    /// so a torn write never yields a half file. Pure file I/O, no Unity types.
    /// </summary>
    public sealed class CoopSaveStore
    {
        public void SaveAtomic(string path, string content)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = path + ".tmp";
            string bak = path + ".bak";
            File.WriteAllText(tmp, content ?? string.Empty, Encoding.UTF8);

            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, bak);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch
            {
                if (File.Exists(tmp))
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch
                    {
                        // best effort cleanup
                    }
                }
                throw;
            }
        }

        public string LoadLatest(string path, Func<string, bool> isValid)
        {
            string main = ReadIfValid(path, isValid);
            if (main != null)
            {
                return main;
            }

            string bakPath = path + ".bak";
            string backup = ReadIfValid(bakPath, isValid);
            if (backup == null)
            {
                return null;
            }

            try
            {
                File.Copy(bakPath, path, true);
            }
            catch
            {
                // backup content is still returned even if restore fails
            }
            return backup;
        }

        private static string ReadIfValid(string path, Func<string, bool> isValid)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                string content = File.ReadAllText(path, Encoding.UTF8);
                if (isValid != null && !isValid(content))
                {
                    return null;
                }
                return content;
            }
            catch
            {
                return null;
            }
        }
    }
}
