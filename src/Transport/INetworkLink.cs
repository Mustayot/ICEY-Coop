using System;

namespace IceyCoop.Transport
{
    /// <summary>
    /// Network transport abstraction used by the session layer. Frames arrive on
    /// the caller's thread only via Poll: OnFrame is never raised from background
    /// threads. Reliable frames are delivered in send order (TCP); unreliable
    /// frames are datagrams (UDP) and may be lost or reordered.
    /// </summary>
    public interface INetworkLink
    {
        bool IsConnected { get; }

        void StartHost(int tcpPort, int udpPort);

        void Connect(string host, int tcpPort, int udpPort);

        void Poll(int maxMessages);

        void SendReliable(byte[] payload);

        void SendUnreliable(byte[] payload);

        void Disconnect();

        /// <summary>Close the current peer while retaining a host's listener.</summary>
        void DisconnectPeer();

        /// <summary>Pin the UDP endpoint of the last polled datagram after authentication.</summary>
        void ConfirmUnreliablePeer();

        event Action OnDisconnected;

        event Action<byte[], bool> OnFrame;

        /// <summary>Diagnostic strings from background IO threads (send/recv failures).
        /// May fire on background threads; consumers must be thread-safe.</summary>
        event Action<string> OnDiagnostic;
    }
}
