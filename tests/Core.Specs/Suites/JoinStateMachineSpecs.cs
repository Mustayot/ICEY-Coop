using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class JoinStateMachineSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "JoinStateMachine";

            s.Tests.Add(new TestCase
            {
                Name = "happy path reaches active",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    m.StartHandshake();
                    m.HandshakeAccepted();
                    m.Loaded();
                    m.BaselineApplied();
                    m.CaughtUp();
                    Assert.Equal(JoinState.Active, m.State, "active");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reject path",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    m.StartHandshake();
                    m.HandshakeRejected();
                    Assert.Equal(JoinState.Rejected, m.State, "rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "resync path",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    m.StartHandshake();
                    m.HandshakeAccepted();
                    m.Loaded();
                    m.BaselineApplied();
                    m.CaughtUp();
                    m.RequestResync();
                    Assert.Equal(JoinState.Resyncing, m.State, "resyncing");
                    m.ResyncBaselineApplied();
                    m.CaughtUp();
                    Assert.Equal(JoinState.Active, m.State, "active again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid transition throws",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    Assert.Throws<InvalidOperationException>(delegate { m.CaughtUp(); }, "CaughtUp from Disconnected must throw");
                    m.StartHandshake();
                    Assert.Throws<InvalidOperationException>(delegate { m.StartHandshake(); }, "double handshake must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "disconnect from any state",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    m.StartHandshake();
                    m.HandshakeAccepted();
                    m.Disconnect();
                    Assert.Equal(JoinState.Disconnected, m.State, "disconnected");
                }
            });
            s.Tests.Add(new TestCase
            {
                Name = "an active session can begin a transition",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    m.StartHandshake();
                    m.HandshakeAccepted();
                    m.Loaded();
                    m.BaselineApplied();
                    m.CaughtUp();
                    Assert.Equal(JoinState.Active, m.State, "active");
                    m.BeginTransition();
                    Assert.Equal(JoinState.Loading, m.State, "back to loading");
                    m.Loaded();
                    Assert.Equal(JoinState.ApplyingBaseline, m.State, "loads again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "transition needs a running session",
                Run = delegate
                {
                    JoinStateMachine m = new JoinStateMachine();
                    bool threw = false;
                    try { m.BeginTransition(); }
                    catch (InvalidOperationException) { threw = true; }
                    Assert.True(threw, "expected InvalidOperationException from Disconnected");
                }
            });

            return s;
        }
    }
}
