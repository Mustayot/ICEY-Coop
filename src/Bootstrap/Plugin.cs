using System;
using BepInEx;
using IceyCoop.Bridge;

namespace IceyCoop.Bootstrap
{
    /// <summary>
    /// Metadata-safe plugin: this assembly must NOT reference Assembly-CSharp,
    /// otherwise the BepInEx chainloader silently drops the dll. The GameBridge
    /// assembly is loaded later, by reflection, from BepInEx/icey-coop/lib.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class IceyCoopPlugin : BaseUnityPlugin
    {
        private const string EnvProjectRoot = "ICEYCOOP_PROJECT_ROOT";

        private int frame;
        private int loadAttempts;
        private bool loadFailed;
        private ICoopBridge bridge;

        private void Awake()
        {
            Marker.Write("bootstrap-awake.txt", DateTime.Now.ToString("o"));
            Logger.LogInfo(string.Format("[icey-coop] Bootstrap Awake. plugin={0} v{1}", PluginInfo.Name, PluginInfo.Version));
            Logger.LogInfo("[icey-coop] Bootstrap metadata-safe probe: this assembly references no Assembly-CSharp types.");
        }

        private void OnEnable()
        {
            Marker.Write("onenable.txt", DateTime.Now.ToString("o"));
            Logger.LogInfo("[icey-coop] Bootstrap OnEnable");
        }

        private void OnDisable()
        {
            Marker.Write("ondisable.txt", DateTime.Now.ToString("o"));
            Logger.LogInfo("[icey-coop] Bootstrap OnDisable");
        }

        private void Start()
        {
            Marker.Write("start.txt", DateTime.Now.ToString("o"));
            Logger.LogInfo("[icey-coop] Bootstrap Start");
            TryStartBridge();
        }

        private void Update()
        {
            frame++;
            if (frame <= 3)
            {
                Marker.Write("update-" + frame + ".txt", DateTime.Now.ToString("o"));
                Logger.LogInfo(string.Format("[icey-coop] Bootstrap Update frame={0}", frame));
            }
            if (bridge == null)
            {
                if (!loadFailed && loadAttempts < 600)
                {
                    TryStartBridge();
                }
                return;
            }
            bridge.OnUpdate();
        }

        private void FixedUpdate()
        {
            if (bridge != null)
            {
                bridge.OnFixedUpdate();
            }
        }

        private void OnGUI()
        {
            if (bridge != null) bridge.OnGUI();
        }

        private void LateUpdate()
        {
            if (bridge != null)
            {
                bridge.OnLateUpdate();
            }
        }

        private void TryStartBridge()
        {
            loadAttempts++;
            Marker.Write("try-bridge-" + loadAttempts + ".txt", DateTime.Now.ToString("o"));
            Logger.LogInfo(string.Format("[icey-coop] TryStartBridge attempt={0}", loadAttempts));
            try
            {
                BootOptions o = BuildOptions();
                ICoopBridge b = PrivateAssemblyLoader.LoadBridge(o);
                if (b == null)
                {
                    throw new InvalidOperationException("LoadBridge returned null");
                }
                b.Initialize(o);
                bridge = b;
                Marker.Write("bridge-loaded.txt", DateTime.Now.ToString("o"));
                Logger.LogInfo("[icey-coop] bridge loaded and initialized");
            }
            catch (Exception ex)
            {
                loadFailed = true;
                Marker.Write("bridge-load-error.txt", ex.ToString());
                Logger.LogError(string.Format("[icey-coop] bridge load failed: {0}", ex));
            }
        }

        private BootOptions BuildOptions()
        {
            string pluginAssemblyPath = typeof(IceyCoopPlugin).Assembly.Location;
            string configuredRoot = Environment.GetEnvironmentVariable(EnvProjectRoot);
            string bepRoot = BootstrapPathResolver.ResolveBepInExRoot(pluginAssemblyPath);
            string root = BootstrapPathResolver.ResolveProjectRoot(configuredRoot, bepRoot);
            string gameRoot = BootstrapPathResolver.ResolveGameRoot(bepRoot);

            BootOptions o = new BootOptions();
            o.ProjectRoot = root;
            o.GameRoot = gameRoot;
            o.LogRoot = BootstrapPathResolver.ResolveLogRoot(root);
            o.EnableHeartbeat = true;

            string role = Environment.GetEnvironmentVariable("ICEYCOOP_ROLE");
            o.Role = string.IsNullOrEmpty(role) ? "offline" : role;

            string connect = Environment.GetEnvironmentVariable("ICEYCOOP_CONNECT");
            if (!string.IsNullOrEmpty(connect))
            {
                o.Connect = connect;
            }

            int parsed;
            string tcpPort = Environment.GetEnvironmentVariable("ICEYCOOP_TCP_PORT");
            if (!string.IsNullOrEmpty(tcpPort) && int.TryParse(tcpPort, out parsed))
            {
                o.TcpPort = parsed;
            }
            string udpPort = Environment.GetEnvironmentVariable("ICEYCOOP_UDP_PORT");
            if (!string.IsNullOrEmpty(udpPort) && int.TryParse(udpPort, out parsed))
            {
                o.UdpPort = parsed;
            }
            string demoInput = Environment.GetEnvironmentVariable("ICEYCOOP_DEMO_INPUT");
            o.DemoInput = demoInput == "1" || demoInput == "true";
            o.ResumeSave = Environment.GetEnvironmentVariable("ICEYCOOP_RESUME_SAVE") == "1";
            o.TcpOnly = Environment.GetEnvironmentVariable("ICEYCOOP_TCP_ONLY") == "1";
            // T23: play through a relay ("ROOMCODE@relayhost:port"); parsed and validated in the
            // bridge, which is the assembly that can see the transport contract.
            string relay = Environment.GetEnvironmentVariable("ICEYCOOP_RELAY");
            o.Relay = string.IsNullOrEmpty(relay) ? string.Empty : relay.Trim();
            return o;
        }

        private void OnDestroy()
        {
            if (bridge != null)
            {
                try
                {
                    bridge.Shutdown();
                }
                catch
                {
                    // never throw from OnDestroy
                }
            }
            Logger.LogInfo("[icey-coop] Bootstrap shutdown");
        }
    }

    internal static class PluginInfo
    {
        public const string Guid = "io.iceycoop.bootstrap";
        public const string Name = "IceyCoop Bootstrap";
        public const string Version = "0.1.0";
    }
}
