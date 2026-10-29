using System;
using System.IO;
using System.Text;

namespace IceyCoop.GameBridge.Diagnostics
{
    /// <summary>
    /// JSONL trace writer. Main-thread only, bounded by callers. Keeps keys out
    /// of the log payload where possible; credentials are never passed here.
    /// Every emitted line is a valid single-line JSON object and carries the
    /// "run" field so that one trace file can be tied back to one boot.
    /// </summary>
    public sealed class TraceWriter
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly string runId;

        public TraceWriter(string logRoot)
        {
            runId = Guid.NewGuid().ToString("N");

            string dir = string.IsNullOrEmpty(logRoot) ? "." : logRoot;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            catch
            {
                // fall back to current directory on failure
                dir = ".";
            }
            string file = "icey-coop-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl";
            path = System.IO.Path.Combine(dir, file);

            // First line: boot marker. Later lines carry the same run id.
            Write("INFO", "run start");
            WriteBuildFingerprint();
        }

        public string Path
        {
            get { return path; }
        }

        public void Info(string message)
        {
            Write("INFO", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        private void Write(string level, string message)
        {
            string line = string.Format("{{\"t\":\"{0:o}\",\"level\":\"{1}\",\"run\":\"{2}\",\"msg\":{3}}}",
                DateTime.Now, level, runId, Quote(message));
            lock (gate)
            {
                try
                {
                    System.IO.File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // never throw from tracing
                }
            }
        }

        /// <summary>
        /// Emits one buildFingerprint line tying the four deployed DLL hashes to
        /// this run. Self-contained: resolves the lib directory from this
        /// assembly's own location and walks up to plugins, so the caller
        /// (ProbeBridge) needs no extra wiring.
        /// </summary>
        private void WriteBuildFingerprint()
        {
            try
            {
                string self = typeof(TraceWriter).Assembly.Location;
                string lib = System.IO.Path.GetDirectoryName(self);
                if (string.IsNullOrEmpty(lib))
                {
                    Write("INFO", "buildFingerprint unavailable: cannot resolve assembly location");
                    return;
                }

                string bepInEx = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(lib));
                string plugins = System.IO.Path.Combine(bepInEx, "plugins");

                string gameBridge = self;
                string core = System.IO.Path.Combine(lib, "IceyCoop.Core.dll");
                string bridge = System.IO.Path.Combine(lib, "IceyCoop.Bridge.dll");
                string bootstrap = System.IO.Path.Combine(plugins, "IceyCoop.Bootstrap.dll");

                string hGameBridge = IceyCoop.GameBridge.BindingManifest.ComputeSha256(gameBridge);
                string hCore = IceyCoop.GameBridge.BindingManifest.ComputeSha256(core);
                string hBridge = IceyCoop.GameBridge.BindingManifest.ComputeSha256(bridge);
                string hBootstrap = IceyCoop.GameBridge.BindingManifest.ComputeSha256(bootstrap);

                Write("INFO", string.Format(
                    "buildFingerprint gameBridge={0} core={1} bridge={2} bootstrap={3} runId={4}",
                    hGameBridge, hCore, hBridge, hBootstrap, runId));
            }
            catch (Exception ex)
            {
                Write("INFO", "buildFingerprint unavailable: " + ex.Message);
            }
        }

        private static string Quote(string s)
        {
            if (s == null)
            {
                return "null";
            }

            StringBuilder sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("X4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
