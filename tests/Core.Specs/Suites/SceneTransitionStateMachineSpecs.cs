using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class SceneTransitionStateMachineSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SceneTransitionStateMachine";

            s.Tests.Add(new TestCase
            {
                Name = "happy path commits and returns active",
                Run = delegate
                {
                    SceneTransitionStateMachine m = new SceneTransitionStateMachine();
                    m.RequestTransition();
                    m.BeginPreparing();
                    m.BeginLoading();
                    m.BindingComplete();
                    m.BarrierReached();
                    m.Commit();
                    m.FinishCommit();
                    Assert.Equal(SceneTransitionState.Active, m.State, "active");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "recovery path",
                Run = delegate
                {
                    SceneTransitionStateMachine m = new SceneTransitionStateMachine();
                    m.RequestTransition();
                    m.BeginPreparing();
                    m.BeginLoading();
                    m.EnterRecovery();
                    Assert.Equal(SceneTransitionState.Recovering, m.State, "recovering");
                    m.RecoverFromCheckpoint();
                    Assert.Equal(SceneTransitionState.Active, m.State, "active");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid transition throws",
                Run = delegate
                {
                    SceneTransitionStateMachine m = new SceneTransitionStateMachine();
                    Assert.Throws<InvalidOperationException>(delegate { m.Commit(); }, "Commit from Active must throw");
                    m.RequestTransition();
                    Assert.Throws<InvalidOperationException>(delegate { m.RequestTransition(); }, "double request must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "end room from any state",
                Run = delegate
                {
                    SceneTransitionStateMachine m = new SceneTransitionStateMachine();
                    m.RequestTransition();
                    m.EndRoom();
                    Assert.Equal(SceneTransitionState.Ended, m.State, "ended");
                }
            });

            return s;
        }
    }
}
