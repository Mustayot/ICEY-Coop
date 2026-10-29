using System;

namespace IceyCoop.Core
{
    public enum SceneTransitionState
    {
        Active = 0,
        TransitionRequested = 1,
        Preparing = 2,
        Loading = 3,
        Binding = 4,
        BaselineBarrier = 5,
        Committed = 6,
        Recovering = 7,
        Ended = 8
    }

    /// <summary>
    /// Scene transition / wipe transaction. Input is frozen after the request,
    /// both ends load the same SceneKey, the host binds and commits a baseline,
    /// then input is released. Failures go to Recovering (restore checkpoint) or
    /// Ended (room closed).
    /// </summary>
    public sealed class SceneTransitionStateMachine
    {
        private SceneTransitionState state;

        public SceneTransitionStateMachine()
        {
            state = SceneTransitionState.Active;
        }

        public SceneTransitionState State
        {
            get { return state; }
        }

        public void RequestTransition()
        {
            Require(SceneTransitionState.Active, "RequestTransition");
            state = SceneTransitionState.TransitionRequested;
        }

        public void BeginPreparing()
        {
            Require(SceneTransitionState.TransitionRequested, "BeginPreparing");
            state = SceneTransitionState.Preparing;
        }

        public void BeginLoading()
        {
            Require(SceneTransitionState.Preparing, "BeginLoading");
            state = SceneTransitionState.Loading;
        }

        public void BindingComplete()
        {
            Require(SceneTransitionState.Loading, "BindingComplete");
            state = SceneTransitionState.Binding;
        }

        public void BarrierReached()
        {
            Require(SceneTransitionState.Binding, "BarrierReached");
            state = SceneTransitionState.BaselineBarrier;
        }

        public void Commit()
        {
            Require(SceneTransitionState.BaselineBarrier, "Commit");
            state = SceneTransitionState.Committed;
        }

        public void FinishCommit()
        {
            Require(SceneTransitionState.Committed, "FinishCommit");
            state = SceneTransitionState.Active;
        }

        public void EnterRecovery()
        {
            if (state != SceneTransitionState.Loading &&
                state != SceneTransitionState.Binding &&
                state != SceneTransitionState.BaselineBarrier)
            {
                throw new InvalidOperationException("EnterRecovery not allowed from " + state);
            }
            state = SceneTransitionState.Recovering;
        }

        public void RecoverFromCheckpoint()
        {
            Require(SceneTransitionState.Recovering, "RecoverFromCheckpoint");
            state = SceneTransitionState.Active;
        }

        public void EndRoom()
        {
            state = SceneTransitionState.Ended;
        }

        private void Require(SceneTransitionState expected, string action)
        {
            if (state != expected)
            {
                throw new InvalidOperationException(string.Format("{0} not allowed from {1}", action, state));
            }
        }
    }
}
