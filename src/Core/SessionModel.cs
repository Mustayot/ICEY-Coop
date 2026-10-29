using System;

namespace IceyCoop.Core
{
    /// <summary>
    /// Per-session counters plus the peer slot state used by the host. Connection
    /// generation and world epoch are monotonic; stale values are rejected by
    /// the callers, not silently wrapped. Heartbeat timing uses the monotonic
    /// Environment.TickCount clock (never Time.timeScale).
    /// </summary>
    public sealed class SessionModel
    {
        private readonly SessionId sessionId;
        private uint connectionGeneration;
        private uint worldEpoch;

        private ConnectionState peerState;
        private uint lastPingTicks;

        public SessionModel(SessionId id)
        {
            sessionId = id;
            connectionGeneration = 0;
            worldEpoch = 0;
            peerState = ConnectionState.Disconnected;
            lastPingTicks = NowTicks();
            HeartbeatTimeoutTicks = 5000; // 5s per the protocol contract
        }

        public SessionId Id
        {
            get { return sessionId; }
        }

        public uint ConnectionGeneration
        {
            get { return connectionGeneration; }
        }

        public uint WorldEpoch
        {
            get { return worldEpoch; }
        }

        public ConnectionState PeerState
        {
            get { return peerState; }
        }

        public uint LastPingTicks
        {
            get { return lastPingTicks; }
        }

        /// <summary>Milliseconds without a ping before the peer is timed out.</summary>
        public uint HeartbeatTimeoutTicks;

        public uint NextConnectionGeneration()
        {
            if (connectionGeneration == uint.MaxValue)
            {
                throw new InvalidOperationException("connection generation overflow");
            }
            connectionGeneration++;
            return connectionGeneration;
        }

        public uint NextWorldEpoch()
        {
            if (worldEpoch == uint.MaxValue)
            {
                throw new InvalidOperationException("world epoch overflow");
            }
            worldEpoch++;
            return worldEpoch;
        }

        public bool IsConnectionCurrent(uint generation)
        {
            return generation == connectionGeneration;
        }

        public bool IsEpochCurrent(uint epoch)
        {
            return epoch == worldEpoch;
        }

        public void SetPeerState(ConnectionState state)
        {
            peerState = state;
        }

        /// <summary>Refreshes the last-ping timestamp on any peer activity.</summary>
        public void TouchPeer(uint nowTicks)
        {
            lastPingTicks = nowTicks;
        }

        public bool IsPeerTimedOut(uint nowTicks)
        {
            return unchecked(nowTicks - lastPingTicks) >= HeartbeatTimeoutTicks;
        }

        public static uint NowTicks()
        {
            return unchecked((uint)Environment.TickCount);
        }
    }
}
