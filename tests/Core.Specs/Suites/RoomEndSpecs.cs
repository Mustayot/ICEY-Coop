using System;
using System.Collections.Generic;
using System.Reflection;
using IceyCoop.Core;
using IceyCoop.GameBridge.Network;
using IceyCoop.Protocol;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    public sealed class RoomEndSpecs
    {
        public Suite Build()
        {
            Suite suite = new Suite();
            suite.Name = "RoomEnd";
            suite.Tests.Add(new TestCase
            {
                Name = "restore exhaustion requests one explicit end and drains once",
                Run = delegate
                {
                    Type requestType = ProductionType("IceyCoop.GameBridge.Actors.RoomEndRequest");
                    Type terminationType = ProductionType("IceyCoop.GameBridge.Network.SessionTermination");
                    object request = Activator.CreateInstance(requestType);
                    object termination = Activator.CreateInstance(terminationType);
                    FakeLink link = new FakeLink();
                    ReconnectTickets tickets = new ReconnectTickets();
                    PendingJoinAdmission pending = new PendingJoinAdmission();
                    SessionId session = new SessionId(Bytes(4));
                    byte[] ticket = tickets.Issue(PlayerIds.P2, session, 2, 0);
                    pending.BeginResume(Bytes(3), ticket, session.ToArray(), 1, 2, 0, 20000);
                    int requests = 0;
                    int neutralized = 0;
                    Action<string> onRequested = delegate(string reason)
                    {
                        requests++;
                        terminationType.GetMethod("EndHost").Invoke(termination, new object[]
                        {
                            link, tickets, pending, new Action(delegate { neutralized++; }), reason, 100u
                        });
                    };
                    requestType.GetEvent("Requested").AddEventHandler(request, onRequested);

                    PartyWipeCoordinator coordinator = new PartyWipeCoordinator(1, 2);
                    coordinator.ReportDowned(PlayerIds.P1, 0);
                    coordinator.ReportDowned(PlayerIds.P2, 0);
                    for (uint tick = 1; tick <= 2; tick++)
                    {
                        Assert.Equal(WipeDecision.RestoreCheckpoint, coordinator.Poll(tick), "each injected restore attempt is real coordinator output");
                        coordinator.ReportRestoreResult(false);
                        coordinator.ReportDowned(PlayerIds.P1, tick);
                        coordinator.ReportDowned(PlayerIds.P2, tick);
                    }
                    for (uint tick = 3; tick < 20; tick++)
                    {
                        requestType.GetMethod("Observe").Invoke(request, new object[] { coordinator.Poll(tick) });
                    }

                    Assert.True((bool)requestType.GetProperty("IsRequested").GetValue(request, null), "request remains latched after failure exhaustion");
                    Assert.Equal(1, requests, "repeated EndRoom polls produce one request event");
                    Assert.True((bool)terminationType.GetProperty("IsEnded").GetValue(termination, null), "session exposes terminal state");
                    Assert.Equal(1, neutralized, "remote inputs neutralized once");
                    Assert.Equal(0, tickets.Count, "all reconnect tickets revoked");
                    Assert.True(!pending.IsPending, "pending admission is revoked");
                    Assert.Equal(1, link.Frames.Count, "one reliable terminal frame is queued");
                    Assert.Equal(MessageKinds.SessionEnded, ReadKind(link.Frames[0]), "terminal frame uses SessionEnded");
                    SessionEndedMessage ended = SessionEndedMessage.Decode(Body(link.Frames[0]));
                    Assert.Equal((byte)SessionEndReason.Error, ended.Reason, "restore exhaustion reports Error");
                    Assert.True(!ended.SaveOk, "failed recovery is not reported as a saved state");

                    terminationType.GetMethod("Poll").Invoke(termination, new object[] { 249u });
                    Assert.Equal(0, link.Disconnects, "transport remains open for 149ms to drain");
                    terminationType.GetMethod("Poll").Invoke(termination, new object[] { 250u });
                    terminationType.GetMethod("Poll").Invoke(termination, new object[] { 251u });
                    Assert.Equal(1, link.Disconnects, "listener and peer close once after the drain window");
                    object duplicate = terminationType.GetMethod("EndHost").Invoke(termination, new object[]
                    {
                        link, tickets, pending, new Action(delegate { neutralized++; }), "duplicate", 300u
                    });
                    Assert.True(!(bool)duplicate, "a duplicate terminal request is inert");
                    Assert.Equal(1, link.Frames.Count, "duplicate request does not enqueue another frame");
                }
            });
            suite.Tests.Add(new TestCase
            {
                Name = "remote end leaves client error visible and closes once",
                Run = delegate
                {
                    Type terminationType = ProductionType("IceyCoop.GameBridge.Network.SessionTermination");
                    object termination = Activator.CreateInstance(terminationType);
                    FakeLink link = new FakeLink();
                    int neutralized = 0;
                    object ended = terminationType.GetMethod("EndClient").Invoke(termination, new object[]
                    {
                        link, new Action(delegate { neutralized++; }), "host ended session: Error; saveOk=False"
                    });
                    Assert.True((bool)ended, "first terminal frame is accepted");
                    Assert.True((bool)terminationType.GetProperty("IsEnded").GetValue(termination, null), "client terminal state is exposed");
                    string error = (string)terminationType.GetProperty("Error").GetValue(termination, null);
                    Assert.True(error.Contains("Error"), "client retains the failure reason for diagnostics");
                    Assert.Equal(1, neutralized, "client input is neutralized");
                    Assert.Equal(1, link.Disconnects, "client closes its connection immediately");
                    object duplicate = terminationType.GetMethod("EndClient").Invoke(termination, new object[]
                    {
                        link, new Action(delegate { neutralized++; }), "duplicate"
                    });
                    Assert.True(!(bool)duplicate, "duplicate terminal frame is ignored");
                    Assert.Equal(1, neutralized, "duplicate end does not neutralize twice");
                    Assert.Equal(1, link.Disconnects, "duplicate end does not disconnect twice");
                }
            });
            return suite;
        }

        private static Type ProductionType(string fullName)
        {
            Type type = Assembly.GetExecutingAssembly().GetType(fullName, false);
            Assert.True(type != null, "production terminal type is linked: " + fullName);
            return type;
        }

        private static byte[] Bytes(byte value)
        {
            byte[] result = new byte[16];
            for (int i = 0; i < result.Length; i++) result[i] = value;
            return result;
        }

        private static ushort ReadKind(byte[] frame)
        {
            return (ushort)(frame[0] | (frame[1] << 8));
        }

        private static byte[] Body(byte[] frame)
        {
            byte[] result = new byte[frame.Length - 2];
            Array.Copy(frame, 2, result, 0, result.Length);
            return result;
        }

        private sealed class FakeLink : INetworkLink
        {
            public readonly List<byte[]> Frames = new List<byte[]>();
            public int Disconnects;
            public bool IsConnected { get { return Disconnects == 0; } }
            public void StartHost(int tcpPort, int udpPort) { }
            public void Connect(string host, int tcpPort, int udpPort) { }
            public void Poll(int maxMessages) { }
            public void SendReliable(byte[] payload) { Frames.Add(payload); }
            public void SendUnreliable(byte[] payload) { }
            public void Disconnect() { Disconnects++; }
            public void DisconnectPeer() { Disconnects++; }
            public void ConfirmUnreliablePeer() { }
            public event Action<byte[], bool> OnFrame { add { } remove { } }
            public event Action<string> OnDiagnostic { add { } remove { } }
            public event Action OnDisconnected { add { } remove { } }
        }
    }
}
