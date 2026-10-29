namespace IceyCoop.Bridge
{
    /// <summary>UI-safe snapshot of the room state. It deliberately contains no Unity types.</summary>
    public sealed class CoopLobbyStatus
    {
        public bool IsMainMenu;
        public bool CanStart;
        public bool CanCancel;
        public bool Busy;
        public bool Connected;
        public bool IsHost;
        public bool IsClient;
        public bool IsListening;
        public string State;
        public string Error;
        public string Peer;
        public string[] HostAddresses;
        public int Port;

        public CoopLobbyStatus()
        {
            State = "Offline";
            Error = string.Empty;
            Peer = string.Empty;
            HostAddresses = new string[0];
            Port = 57000;
        }
    }

    /// <summary>
    /// Main-thread controls consumed by the lobby view. Implementations own all
    /// transport, role changes, and safe return-to-title behavior.
    /// </summary>
    public interface ICoopLobbyActions
    {
        CoopLobbyStatus GetLobbyStatus();
        void CreateRoom(int port);
        void JoinRoom(string address);
        void CloseRoom();
    }
}
