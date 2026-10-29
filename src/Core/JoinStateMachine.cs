using System;

namespace IceyCoop.Core
{
    public enum JoinState
    {
        Disconnected = 0,
        Handshaking = 1,
        Loading = 2,
        ApplyingBaseline = 3,
        CatchingUp = 4,
        Active = 5,
        Rejected = 6,
        Resyncing = 7
    }

    /// <summary>
    /// Client join transaction: only legal transitions advance the state. Input
    /// is not enabled until Active; errors land in Rejected/Resyncing.
    /// </summary>
    public sealed class JoinStateMachine
    {
        private JoinState state;

        public JoinStateMachine()
        {
            state = JoinState.Disconnected;
        }

        public JoinState State
        {
            get { return state; }
        }

        public void StartHandshake()
        {
            Require(JoinState.Disconnected, "StartHandshake");
            state = JoinState.Handshaking;
        }

        public void HandshakeAccepted()
        {
            Require(JoinState.Handshaking, "HandshakeAccepted");
            state = JoinState.Loading;
        }

        public void HandshakeRejected()
        {
            Require(JoinState.Handshaking, "HandshakeRejected");
            state = JoinState.Rejected;
        }

        public void LoadFailed()
        {
            Require(JoinState.Loading, "LoadFailed");
            state = JoinState.Rejected;
        }

        public void Loaded()
        {
            Require(JoinState.Loading, "Loaded");
            state = JoinState.ApplyingBaseline;
        }

        public void BaselineApplied()
        {
            Require(JoinState.ApplyingBaseline, "BaselineApplied");
            state = JoinState.CatchingUp;
        }

        public void CaughtUp()
        {
            Require(JoinState.CatchingUp, "CaughtUp");
            state = JoinState.Active;
        }

        /// <summary>
        /// Host-initiated scene transition while the session is already running: the peer
        /// drops back to Loading so the normal Load → Baseline → CaughtUp chain still
        /// guards it. Without this a second ScenePrepare is an illegal transition and the
        /// peer silently stays in the old room (observed: client stuck in C2L1S7 while the
        /// host had already moved to C3L1S3).
        /// </summary>
        public void BeginTransition()
        {
            if (state != JoinState.Active && state != JoinState.CatchingUp)
            {
                throw new InvalidOperationException("BeginTransition not allowed from " + state);
            }
            state = JoinState.Loading;
        }
        public void RequestResync()
        {
            if (state != JoinState.CatchingUp && state != JoinState.Active)
            {
                throw new InvalidOperationException("RequestResync not allowed from " + state);
            }
            state = JoinState.Resyncing;
        }

        public void ResyncBaselineApplied()
        {
            Require(JoinState.Resyncing, "ResyncBaselineApplied");
            state = JoinState.CatchingUp;
        }

        public void Disconnect()
        {
            state = JoinState.Disconnected;
        }

        private void Require(JoinState expected, string action)
        {
            if (state != expected)
            {
                throw new InvalidOperationException(string.Format("{0} not allowed from {1}", action, state));
            }
        }
    }
}
