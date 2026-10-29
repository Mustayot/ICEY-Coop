using System;

namespace IceyCoop.Bridge
{
    /// <summary>
    /// Pure contract assembly. Must not reference UnityEngine, BepInEx or
    /// Assembly-CSharp so it can be shared between Bootstrap and GameBridge.
    /// C# 5 only (game runs on old Mono / mscorlib 2.0.5.0).
    /// </summary>
    public sealed class BootOptions
    {
        public string ProjectRoot { get; set; }
        public string GameRoot { get; set; }
        public string LogRoot { get; set; }
        public bool EnableHeartbeat { get; set; }

        // G3 role/connect overrides, read from environment in Bootstrap.
        public string Role { get; set; }       // offline | host | client
        public string Connect { get; set; }    // "host:tcpPort" for the client
        public int TcpPort { get; set; }
        public int UdpPort { get; set; }
        public bool DemoInput { get; set; }    // client demo axis (ICEYCOOP_DEMO_INPUT=1)
        public bool ResumeSave { get; set; }   // explicit host startup restore (ICEYCOOP_RESUME_SAVE=1)
        public bool TcpOnly { get; set; }      // single-port compatibility tunnel for menu rooms
        /// <summary>
        /// T23: "ROOMCODE@relayhost:port" (ICEYCOOP_RELAY). When set, both roles dial the relay and
        /// nobody listens on a local port, so a session works without port forwarding.
        /// </summary>
        public string Relay { get; set; }

        public BootOptions()
        {
            ProjectRoot = string.Empty;
            GameRoot = string.Empty;
            LogRoot = string.Empty;
            EnableHeartbeat = true;
            Role = "offline";
            Connect = "127.0.0.1:57000";
            TcpPort = 57000;
            UdpPort = 57001;
            DemoInput = false;
            Relay = string.Empty;
        }
    }

    public interface ICoopBridge
    {
        void Initialize(BootOptions options);
        void OnUpdate();
        void OnFixedUpdate();
        void OnLateUpdate();
        void OnGUI();
        void Shutdown();
    }
}
