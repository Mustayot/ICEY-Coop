using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using IceyCoop.Bridge;
using IceyCoop.Core;
using IceyCoop.GameBridge.Combat;
using IceyCoop.GameBridge.Presentation;
using IceyCoop.GameBridge.Story;
using IceyCoop.GameBridge.World;
using IceyCoop.Protocol;
using IceyCoop.Transport;
using UnityEngine;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>
    /// G3 session orchestrator over the formal T12 transport. Single-threaded on
    /// the main thread; TcpUdpLink does blocking IO on its own threads and only
    /// surfaces frames via Poll. Frames on the wire carry a 2-byte MessageKinds
    /// prefix followed by the Messages codec payload. Host state machine keeps
    /// the T15 baseline stages as explicit values but jumps to Active after the
    /// UDP challenge (input-loop proof first).
    /// </summary>
    public sealed class SessionOrchestrator
    {
        public enum Role { Offline, Host, Client }

        // Not in MessageKinds (1-23): T13 UDP challenge frame. Revisit when the
        // 03 contract adds a dedicated challenge message.
        private const ushort ChallengeKind = 100;

        private enum HostState { Idle, Listening, P2Joining, P2Loading, ApplyingBaseline, CatchingUp, Active, Rejected, Resyncing, Ended }

        private INetworkLink link;
        private Role role = Role.Offline;
        private HostState hostState = HostState.Idle;
        private JoinStateMachine clientJoin = new JoinStateMachine();
        private SessionModel session;
        private byte[] challengeNonce;
        private byte[] remoteSessionId;
        private uint clientConnectionGeneration;
        private byte[] clientReconnectTicket = new byte[0];
        private string clientTicketPath;
        private string clientHost;
        private int clientTcpPort;
        private int clientUdpPort;
        private readonly ReconnectTickets reconnectTickets = new ReconnectTickets();
        private readonly PendingJoinAdmission pendingAdmission = new PendingJoinAdmission();
        private readonly SessionTermination termination = new SessionTermination();
        private uint lastTicketTicks;
        private bool closeRejectedPeer;
        private uint closeRejectedAt;
        private uint handshakeStartedTicks;
        private bool waitingForHello;
        private const uint TicketRefreshMs = 10000;
        private uint lastHeartbeatTicks;
        private uint lastInputTicks;
        private uint lastChallengeTicks;
        private bool helloSent;
        private bool peerEverActive;
        private string lastError;
        private string gameRoot;
        private int hostTcpPort;
        private int hostUdpPort;
        private bool listenerStarted;
        private JoinTransaction joinTransaction;
        private uint clientBaselineId;
        private uint pendingBaselineId;
        private ulong clientBaselineIncludedSeq;
        private int hostInputFrameCount;
        private int hostPongCount;
        private int clientPingCount;
        private int clientAckCount;
        private readonly SnapshotFreshnessGate snapshotFreshness = new SnapshotFreshnessGate();
        private ulong clientBaselineHostTick;
        private uint clientInputSentTicks;
        public ulong PendingSnapshotEvents { get; private set; }
        private ulong clientInputSentSeq;
        private ulong clientInputAckedSeq;
        private uint clientInputAckTicks;
        private bool hasClientInputAck;
        private int clientRejectedAckCount;
        private ulong clientSnapshotsReceived;
        private uint clientSnapshotTicks;
        private bool hasClientSnapshot;
        private uint telemetryEpoch;
        private bool telemetryEpochSet;
        private bool telemetryWasActive;
        private uint telemetryActiveSince;
        private ReplicaStore replicaStore = new ReplicaStore();
        private byte[] clientBaselineData;
        private uint clientBaselineNextChunk;
        private uint clientBaselineChunkCount;
        private uint clientBaselineLength;
        private byte[] clientBaselineDigest;
        private readonly List<EntitySnapshot> clientBaselineEntities = new List<EntitySnapshot>();
        private uint clientBaselineEpoch;
        private ulong hostEventSeq;
        private ulong hostTick;
        private ulong snapshotId;
        private bool endpointBound;
        private string preparedScene;
        private uint sceneTransitionId;
        private string lastHostScene;
        // T22: host-side reconnect window (see Core/ReconnectWindow).
        private IceyCoop.Core.ReconnectWindow reconnectWindow = new IceyCoop.Core.ReconnectWindow();
        private bool waitingSceneLogged;
        private string lastSceneDecisionLogged;
        private int lastIsolationSampleFrame;
        private int lastIsolationSweepFrame;

        /// <summary>Reviewed join scenes; empty means nothing has been audited yet.</summary>
        private static readonly string sceneWhitelistCsv = Environment.GetEnvironmentVariable(SceneWhitelist.EnvList);

        /// <summary>Strict mode: only whitelisted scenes may join.</summary>
        private static readonly bool sceneStrict = Environment.GetEnvironmentVariable(SceneWhitelist.EnvStrict) == "1";

        /// <summary>
        /// Deliberate mixed-build test joins only. Default is to reject: a build
        /// difference means the wire format may differ (EntitySnapshot gained
        /// PrefabKey without a version bump once already).
        /// </summary>
        private static readonly bool allowBuildMismatch = Environment.GetEnvironmentVariable("ICEYCOOP_ALLOW_BUILD_MISMATCH") == "1";

        /// <summary>How long the client may take to align the host's scene.</summary>
        private const uint SceneLoadTimeoutMs = 30000;
        /// <summary>The boot sequence (splash → ui_start) can swallow a load issued during splash, so the auto-load is re-issued until it sticks.</summary>
        private const uint SceneLoadRetryMs = 2500;

        /// <summary>Frames the active scene must stay matched before SceneReady.</summary>
        private const int SceneReadySettleFrames = 3;

        /// <summary>Operator escape hatch: refuse to auto-load instead of loading.</summary>
        private static readonly bool disableAutoScene = Environment.GetEnvironmentVariable("ICEYCOOP_NO_AUTO_SCENE") == "1";

        private string pendingSceneKey;
        private uint pendingTransitionId;
        private bool sceneLoadInFlight;
        private int sceneReadySettleFrames;
        private uint sceneLoadDeadlineTicks;
        private long sceneLoadRetryTicks;

        public Role CurrentRole
        {
            get { return role; }
        }

        public string LastError
        {
            get { return lastError; }
        }

        public JoinState ClientState { get { return clientJoin.State; } }

        public void FillHealthSample(IceyCoop.Core.RuntimeHealthSample sample)
        {
            if (sample == null) return;
            uint now = SessionModel.NowTicks();
            bool active = IsActive;
            if (active)
            {
                if (!telemetryWasActive)
                {
                    telemetryActiveSince = now;
                    telemetryWasActive = true;
                }
                sample.ActiveSeconds = unchecked(now - telemetryActiveSince) / 1000d;
            }
            else
            {
                telemetryWasActive = false;
                telemetryActiveSince = 0;
                sample.ActiveSeconds = 0d;
            }
            sample.Role = role.ToString();
            sample.State = role == Role.Host ? hostState.ToString() : role == Role.Client ? clientJoin.State.ToString() : "Offline";
            sample.Active = active;
            sample.Loading = role == Role.Host
                ? hostState == HostState.P2Loading || hostState == HostState.ApplyingBaseline || hostState == HostState.CatchingUp || hostState == HostState.Resyncing
                : role == Role.Client && (clientJoin.State == JoinState.Loading || clientJoin.State == JoinState.ApplyingBaseline || clientJoin.State == JoinState.CatchingUp || clientJoin.State == JoinState.Resyncing);
            sample.LastError = lastError;
            if (role == Role.Host)
            {
                sample.InputAccepted = InputAdapter.LastAcceptedSeq(PlayerIds.P2);
                sample.InputAgeSeconds = InputAdapter.GetInputAgeSeconds(PlayerIds.P2);
                sample.InputSent = 0;
                sample.InputAcked = 0;
                sample.AckAgeSeconds = -1d;
                sample.SnapshotsReceived = 0;
                sample.SnapshotAgeSeconds = -1d;
            }
            else if (role == Role.Client)
            {
                sample.InputAccepted = 0;
                sample.InputAgeSeconds = -1d;
                sample.InputSent = clientInputSentSeq;
                sample.InputAgeSeconds = clientInputSentSeq == 0 ? -1d : unchecked(now - clientInputSentTicks) / 1000d;
                sample.InputAcked = clientInputAckedSeq;
                sample.AckAgeSeconds = hasClientInputAck ? unchecked(now - clientInputAckTicks) / 1000d : -1d;
                sample.SnapshotsReceived = clientSnapshotsReceived;
                sample.SnapshotAgeSeconds = hasClientSnapshot ? unchecked(now - clientSnapshotTicks) / 1000d : -1d;
            }
            else
            {
                sample.InputAccepted = 0;
                sample.InputAgeSeconds = -1d;
                sample.InputSent = 0;
                sample.InputAcked = 0;
                sample.AckAgeSeconds = -1d;
                sample.SnapshotsReceived = 0;
                sample.SnapshotAgeSeconds = -1d;
            }
        }

        private void ResetSessionState()
        {
            snapshotFreshness.Reset();
            clientBaselineHostTick = 0;
            clientInputSentTicks = 0;
            PendingSnapshotEvents = 0;
            Actors.G25DeathPatches.RoomEndRequested -= OnRoomEndRequested;
            Progression.PartyCheckpointRestore.FailureRequested -= OnCheckpointRestoreFailureRequested;
            Progression.PartyRevive.FailureRequested -= OnPartnerReviveFailureRequested;
            Progression.PartyRevive.Reset();
            Progression.PartyCheckpointRestore.Reset();
            Progression.HostStartupResume.Reset();
            listenerStarted = false;
            lastHostScene = null;
            sceneTransitionId = 0;
            termination.Reset();
            reconnectTickets.Clear();
            pendingAdmission.Confirm();
            reconnectWindow = new IceyCoop.Core.ReconnectWindow();
            role = Role.Offline;
            hostState = HostState.Idle;
            session = null;
            clientJoin = new JoinStateMachine();
            peerEverActive = false;
            // T19: a fresh session has no detached peer to account for.
            BridgeContext.PeerDetached = false;
            clientReconnectTicket = new byte[0];
            clientTicketPath = null;
            clientHost = null;
            clientTcpPort = 0;
            clientUdpPort = 0;
            remoteSessionId = null;
            challengeNonce = null;
            clientConnectionGeneration = 0;
            clientBaselineEpoch = 0;
            clientBaselineId = 0;
            pendingBaselineId = 0;
            clientBaselineIncludedSeq = 0;
            clientBaselineNextChunk = 0;
            clientBaselineChunkCount = 0;
            clientBaselineLength = 0;
            clientBaselineDigest = null;
            joinTransaction = null;
            clientBaselineData = null;
            clientBaselineEntities.Clear();
            replicaStore = new ReplicaStore();
            hostEventSeq = 0;
            hostTick = 0;
            snapshotId = 0;
            endpointBound = false;
            preparedScene = null;
            waitingForHello = false;
            closeRejectedPeer = false;
            sceneLoadInFlight = false;
            pendingSceneKey = null;
            pendingTransitionId = 0;
            sceneReadySettleFrames = 0;
            sceneLoadDeadlineTicks = 0;
            sceneLoadRetryTicks = 0;
            lastError = null;
            clientInputSentSeq = 0;
            clientInputAckedSeq = 0;
            clientInputAckTicks = 0;
            hasClientInputAck = false;
            clientAckCount = 0;
            clientRejectedAckCount = 0;
            clientSnapshotsReceived = 0;
            clientSnapshotTicks = 0;
            hasClientSnapshot = false;
            telemetryEpoch = 0;
            telemetryEpochSet = false;
            telemetryWasActive = false;
            telemetryActiveSince = 0;
            InputAdapter.ResetRemoteInput(PlayerIds.P1);
            InputAdapter.ResetRemoteInput(PlayerIds.P2);
            EntityAdapter.HostEventSink = null;
            Progression.CoopSaveWriter.WorldEpochProvider = null;
        }

        public bool IsActive
        {
            get
            {
                if (IsEnded) return false;
                if (role == Role.Host) return hostState == HostState.Active;
                if (role == Role.Client) return clientJoin.State == JoinState.Active;
                return false;
            }
        }

        public bool IsEnded { get { return termination.IsEnded; } }
        public bool IsListening { get { return role == Role.Host && listenerStarted && !IsEnded; } }
        public HostResumeState StartupResumeState { get { return Progression.HostStartupResume.State; } }

        public void StartHost(INetworkLink port, BootOptions o)
        {
            if (link != null) Shutdown();
            ResetSessionState();
            link = port;
            role = Role.Host;
            gameRoot = o.GameRoot;
            hostTcpPort = o.TcpPort;
            hostUdpPort = o.UdpPort;
            session = new SessionModel(new SessionId(NewSessionId()));
            Progression.CoopSaveWriter.WorldEpochProvider = delegate { return session.WorldEpoch; };
            InputAdapter.ResetRemoteInput(PlayerIds.P2);
            hostState = HostState.Listening;
            hostEventSeq = 0;
            hostTick = 0;
            snapshotId = 0;
            lastHeartbeatTicks = SessionModel.NowTicks();
            BridgeContext.SetRole(false, true, false);
            Actors.G25DeathPatches.Reset();
            Actors.G25DeathPatches.RoomEndRequested -= OnRoomEndRequested;
            Actors.G25DeathPatches.RoomEndRequested += OnRoomEndRequested;
            Progression.PartyCheckpointRestore.FailureRequested -= OnCheckpointRestoreFailureRequested;
            Progression.PartyCheckpointRestore.FailureRequested += OnCheckpointRestoreFailureRequested;
            Progression.PartyRevive.FailureRequested -= OnPartnerReviveFailureRequested;
            Progression.PartyRevive.FailureRequested += OnPartnerReviveFailureRequested;
            EntityAdapter.HostEventSink = OnHostWorldEvent;
            JoinTransaction.StorySnapshotProvider = StoryCapture.CurrentSnapshotBytes;

            port.OnFrame += OnHostFrame;
            port.OnDiagnostic += OnLinkDiagnostic;
            port.OnDisconnected += OnHostDisconnected;
            Progression.HostStartupResume.Begin(o.ResumeSave);
            if (Progression.HostStartupResume.State == HostResumeState.Failed)
            {
                EndHostSession("startup resume failed: " + Progression.HostStartupResume.LastError);
                return;
            }
            if (Progression.HostStartupResume.CanListen) StartHostListener();
        }

        private void StartHostListener()
        {
            if (listenerStarted || IsEnded || role != Role.Host
                || !Progression.HostStartupResume.CanListen) return;
            try
            {
                link.StartHost(hostTcpPort, hostUdpPort);
                listenerStarted = true;
            }
            catch (SocketException ex)
            {
                EndHostSession("bind failed: " + ex.Message);
                return;
            }
            BridgeContext.Info(string.Format("[t13] host listening tcp={0} udp={1}", hostTcpPort, hostUdpPort));
        }

        public void StartClient(INetworkLink port, BootOptions o)
        {
            if (link != null) Shutdown();
            ResetSessionState();
            link = port;
            role = Role.Client;
            Progression.CoopSaveWriter.WorldEpochProvider = null;
            gameRoot = o.GameRoot;
            clientJoin = new JoinStateMachine();
            clientJoin.StartHandshake();
            helloSent = false;
            InputSampler.DemoMode = o.DemoInput;
            lastHeartbeatTicks = SessionModel.NowTicks();
            lastInputTicks = lastHeartbeatTicks;
            BridgeContext.SetRole(true, false, false);
            JoinTransaction.StorySnapshotProvider = null;
            Actors.G25DeathPatches.Reset();

            string host = "127.0.0.1";
            int tcpPort = o.TcpPort;
            if (!string.IsNullOrEmpty(o.Connect))
            {
                string[] parts = o.Connect.Split(':');
                if (parts.Length >= 1 && parts[0].Length > 0)
                {
                    host = parts[0];
                }
                if (parts.Length >= 2)
                {
                    int parsed;
                    if (int.TryParse(parts[1], out parsed))
                    {
                        tcpPort = parsed;
                    }
                }
            }

            port.OnFrame += OnClientFrame;
            port.OnDiagnostic += OnLinkDiagnostic;
            port.OnDisconnected += OnClientDisconnected;
            clientHost = host;
            clientTcpPort = tcpPort;
            clientUdpPort = o.UdpPort;
            clientTicketPath = ReconnectCredentialStore.GetPath(gameRoot, host, tcpPort);
            LoadClientCredential();
            try
            {
                port.Connect(host, tcpPort, o.UdpPort);
            }
            catch (SocketException ex)
            {
                lastError = "connect failed: " + ex.Message;
                BridgeContext.Error("[t13] " + lastError);
                return;
            }
            BridgeContext.Info(string.Format("[t13] client connecting {0}:{1} udp={2}", host, tcpPort, o.UdpPort));
        }

        public void Poll()
        {
            if (link == null)
            {
                return;
            }
            if (role == Role.Host && !IsEnded && !listenerStarted)
            {
                Progression.HostStartupResume.Tick();
                if (Progression.HostStartupResume.State == HostResumeState.Failed)
                    EndHostSession("startup resume failed: " + Progression.HostStartupResume.LastError);
                else if (Progression.HostStartupResume.CanListen)
                    StartHostListener();
                // No listener/peer means no baseline, scene transaction or input may
                // run while the startup world is still being restored.
                if (!listenerStarted && !IsEnded) return;
            }
            link.Poll(32);

            uint now = SessionModel.NowTicks();
            if (IsEnded)
            {
                termination.Poll(now);
                return;
            }

            if (role == Role.Client)
            {
                if (clientJoin.State == JoinState.Handshaking && link.IsConnected && !helloSent)
                {
                    helloSent = true;
                    SendHello();
                }
                if (clientJoin.State == JoinState.Active)
                {
                    InputSampler.SampleLocal();
                    if (now - lastInputTicks >= 16)
                    {
                        lastInputTicks = now;
                        SendInputFrame(InputSampler.BuildFrame());
                    }
                }
                if (clientJoin.State == JoinState.Loading && remoteSessionId != null && unchecked(now - lastChallengeTicks) >= 500)
                    SendChallenge();
                if (Time.frameCount - lastIsolationSampleFrame >= 600)
                {
                    lastIsolationSampleFrame = Time.frameCount;
                    LogIsolationSample();
                }
                if (Time.frameCount - lastIsolationSweepFrame >= 60)
                {
                    lastIsolationSweepFrame = Time.frameCount;
                    ClientIsolation.Sweep();
                }
                if (sceneLoadInFlight)
                {
                    PollSceneLoad();
                }
                return;
            }

            if (role == Role.Host)
            {
                // Includes UDP binding, scene readiness and baseline acknowledgement;
                // a resume keeps the deadline of the original disconnected slot.
                if (hostState != HostState.Active && pendingAdmission.IsExpired(now))
                {
                    BridgeContext.Info("[t22] preActive admission deadline expired");
                    OnHostDisconnected();
                    pendingAdmission.Confirm();
                    link.DisconnectPeer();
                }
                if (closeRejectedPeer && unchecked(now - closeRejectedAt) >= 150)
                {
                    closeRejectedPeer = false;
                    link.DisconnectPeer();
                }
                if (reconnectWindow.Poll(now))
                {
                    reconnectTickets.Clear();
                    pendingAdmission.Confirm();
                    hostState = HostState.Listening;
                    BridgeContext.Info("[t22] reconnect window expired; releasing the slot without rebuilding the world");
                }
                if (hostState == HostState.Listening && link.IsConnected && !waitingForHello)
                {
                    waitingForHello = true;
                    handshakeStartedTicks = now;
                }
                if (waitingForHello && unchecked(now - handshakeStartedTicks) >= 5000)
                {
                    waitingForHello = false;
                    link.DisconnectPeer();
                }
                if (reconnectWindow.State == SlotState.Occupied && !pendingAdmission.IsPending && !closeRejectedPeer && link.IsConnected && unchecked(now - lastTicketTicks) >= TicketRefreshMs)
                {
                    IssueTicketUpdate(now);
                }
                reconnectTickets.PurgeExpired(now);
                if(hostState==HostState.P2Loading && endpointBound && preparedScene==null)
                {
                    string scene=LevelManager.SceneName;
                    if(IsJoinScene(scene) && BridgeContext.P2Root!=null && P2BindingReady())
                    {
                        preparedScene=scene;
                        ScenePrepareMessage prepare=new ScenePrepareMessage();
                        prepare.TransitionId=++sceneTransitionId;
                        prepare.NextEpoch=session.WorldEpoch;
                        prepare.SceneKey=scene;
                        prepare.CheckpointKey=string.Empty;
                        prepare.Reason=(byte)SceneEnterReason.Gate;
                        SendReliable(MessageKinds.ScenePrepare,prepare.Encode());
                        BridgeContext.Info("[join] waiting client SceneReady scene="+scene);
                    }
                    else if(!waitingSceneLogged)
                    {
                        waitingSceneLogged=true;
                        BridgeContext.Info("[join] host is not ready to prepare a scene (scene gate, P2 root or native binding); peer remains Loading, no baseline");
                    }
                }
                PollHostSceneChange();
                if (joinTransaction != null)
                {
                    joinTransaction.Poll(now);
                    if (joinTransaction.CurrentState == JoinTransaction.State.Cancelled)
                    {
                        OnHostDisconnected();
                        link.DisconnectPeer();
                    }
                }
                if (hostState == HostState.Active && now - lastHeartbeatTicks >= 1000)
                {
                    lastHeartbeatTicks = now;
                    PingMessage ping = new PingMessage();
                    ping.Timestamp = now;
                    ping.ConnectionGeneration = session.ConnectionGeneration;
                    SendReliable(MessageKinds.Ping, ping.Encode());
                }
                if (peerEverActive && session != null && hostState==HostState.Active)
                {
                    uint silent = now - session.LastPingTicks;
                    if (silent >= 15000 && session.PeerState != ConnectionState.Disconnected)
                    {
                        // T22: do not tear the world down on the spot. Hold the slot for the
                        // grace window so the same peer can come back into the running session;
                        // the world is only rebuilt once the window expires.
                        OnHostDisconnected();
                        link.DisconnectPeer();
                    }
                    else if (silent >= 5000 && session.PeerState == ConnectionState.Active)
                    {
                        session.SetPeerState(ConnectionState.Suspended);
                        // T19: a suspended peer must not hold the party gate.
                        BridgeContext.PeerDetached = true;
                        BridgeContext.Info("[t13] peer suspended");
                    }
                }
            }
        }

        public void Shutdown()
        {
            Actors.G25DeathPatches.RoomEndRequested -= OnRoomEndRequested;
            Progression.PartyCheckpointRestore.FailureRequested -= OnCheckpointRestoreFailureRequested;
            Progression.PartyRevive.FailureRequested -= OnPartnerReviveFailureRequested;
            if (link != null)
            {
                try
                {
                    link.OnFrame -= OnHostFrame;
                    link.OnFrame -= OnClientFrame;
                    link.OnDiagnostic -= OnLinkDiagnostic;
                    link.OnDisconnected -= OnHostDisconnected;
                    link.OnDisconnected -= OnClientDisconnected;
                    link.Disconnect();
                }
                catch { }
            }
            link = null;
            ResetSessionState();
            Progression.CoopSaveWriter.WorldEpochProvider = null;
            Actors.G25DeathPatches.Reset();
            BridgeContext.SetRole(false, true, true);
        }

        // ---- host ----

        private static void NeutralizeRemoteInputs()
        {
            InputAdapter.ResetRemoteInput(PlayerIds.P1);
            InputAdapter.ResetRemoteInput(PlayerIds.P2);
        }

        private void OnRoomEndRequested(string reason)
        {
            EndHostSession(reason);
        }

        private void OnCheckpointRestoreFailureRequested(string reason)
        {
            EndHostSession("team checkpoint restore failed: " + reason);
        }

        /// <summary>
        /// T20: a committed partner revive could not be confirmed or rolled back. The room ends
        /// honestly instead of leaving a half-revived player in it.
        /// </summary>
        private void OnPartnerReviveFailureRequested(string reason)
        {
            EndHostSession("partner revive failed: " + reason);
        }

        private void EndHostSession(string reason)
        {
            if (role != Role.Host || IsEnded) return;
            if (!termination.EndHost(link, reconnectTickets, pendingAdmission, NeutralizeRemoteInputs,
                reason, SessionModel.NowTicks())) return;

            lastError = reason;
            hostState = HostState.Ended;
            Progression.HostStartupResume.CancelPending(reason);
            if (session != null) session.SetPeerState(ConnectionState.Disconnected);
            joinTransaction = null;
            endpointBound = false;
            challengeNonce = null;
            preparedScene = null;
            waitingForHello = false;
            closeRejectedPeer = false;
            EntityAdapter.HostEventSink = null;
            Progression.CoopSaveWriter.WorldEpochProvider = null;
            // A wipe can end the room while a prior restore is still waiting. Cancel that
            // pending work, while preserving a failed gate's LastError for diagnostics.
            if (Progression.PartyCheckpointRestore.Armed)
                Progression.PartyCheckpointRestore.Reset();
            BridgeContext.Error("[session] Ended: " + reason);
        }

        private void HandleClientSessionEnded(byte[] body)
        {
            SessionEndedMessage message = SessionEndedMessage.Decode(body);
            string reason = "host ended session: " + (SessionEndReason)message.Reason + "; saveOk=" + message.SaveOk;
            if (!termination.EndClient(link, NeutralizeRemoteInputs, reason)) return;

            lastError = reason;
            clientJoin.Disconnect();
            sceneLoadInFlight = false;
            clientReconnectTicket = new byte[0];
            clientBaselineData = null;
            clientBaselineEntities.Clear();
            replicaStore = new ReplicaStore();
            remoteSessionId = null;
            challengeNonce = null;
            try
            {
                if (!string.IsNullOrEmpty(clientTicketPath) && File.Exists(clientTicketPath))
                    File.Delete(clientTicketPath);
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[session] ended credential could not be removed: " + ex.Message);
            }
            BridgeContext.Error("[session] Ended: " + reason);
        }

        private void OnHostDisconnected()
        {
            if (IsEnded) return;
            waitingForHello = false;
            closeRejectedPeer = false;
            if (session == null || reconnectWindow.State != SlotState.Occupied) return;
            InputAdapter.ResetRemoteInput(PlayerIds.P2);
            session.SetPeerState(ConnectionState.Disconnected);
            // T19: the peer is gone; the party gate must stop waiting for it (the reconnect window
            // still exists, but a door walk in the meantime should not stall for 20s).
            BridgeContext.PeerDetached = true;
            uint lostAt = SessionModel.NowTicks();
            if (pendingAdmission.IsPending)
            {
                lostAt = pendingAdmission.GraceStartedAt;
                pendingAdmission.Detach();
            }
            else pendingAdmission.Confirm();
            reconnectWindow.PeerLost(lostAt);
            hostState = HostState.Listening;
            joinTransaction = null;
            endpointBound = false;
            challengeNonce = null;
            preparedScene = null;
            BridgeContext.Info(string.Format("[t22] peer lost; holding slot for {0}ms generation={1} session={2} epoch={3}",
                reconnectWindow.GraceMs, reconnectWindow.Generation, session.Id, session.WorldEpoch));
        }

        private void OnClientDisconnected()
        {
            if (IsEnded) return;
            clientJoin.Disconnect();
            sceneLoadInFlight = false;
            BridgeContext.Info("[t22] client disconnected; credential retained for Reconnect or next process");
        }

        /// <summary>Retry a short interruption in this process. A restarted client
        /// automatically loads the same host-specific credential in StartClient.</summary>
        public bool Reconnect()
        {
            if (IsEnded || role != Role.Client || link == null || link.IsConnected) return false;
            link.Disconnect();
            clientJoin = new JoinStateMachine(); clientJoin.StartHandshake();
            helloSent = false; sceneLoadInFlight = false;
            clientBaselineData = null; replicaStore = new ReplicaStore();
            LoadClientCredential();
            try { link.Connect(clientHost, clientTcpPort, clientUdpPort); return true; }
            catch (Exception ex) { lastError = "reconnect failed: " + ex; BridgeContext.Error("[t22] " + lastError); return false; }
        }

        private void LoadClientCredential()
        {
            try { clientReconnectTicket = ReconnectCredentialStore.Load(clientTicketPath, clientHost, clientTcpPort); }
            catch (Exception ex)
            {
                clientReconnectTicket = new byte[0];
                BridgeContext.Error("[t22] credential read failed: " + ex);
            }
        }

        private void SaveClientCredential(byte[] ticket)
        {
            clientReconnectTicket = ticket;
            try
            {
                ReconnectCredentialStore.Save(clientTicketPath, clientHost, clientTcpPort,
                    remoteSessionId, clientConnectionGeneration, ticket);
                BridgeContext.Info("[t22] client credential persisted generation=" + clientConnectionGeneration);
            }
            catch (Exception ex) { BridgeContext.Error("[t22] credential persistence failed: " + ex); }
        }

        private void IssueTicketUpdate(uint now)
        {
            ReconnectTicketMessage update = new ReconnectTicketMessage();
            update.SessionId = session.Id.ToArray();
            update.ConnectionGeneration = session.ConnectionGeneration;
            update.Ticket = reconnectTickets.Issue(PlayerIds.P2, session.Id, session.ConnectionGeneration, now);
            lastTicketTicks = now;
            SendReliable(MessageKinds.ReconnectTicket, update.Encode());
        }

        private void OnHostFrame(byte[] payload, bool reliable)
        {
            if (IsEnded) return;
            try
            {
                ushort kind = ReadKind(payload);
                byte[] body = ReadBody(payload);
                if ((kind == ChallengeKind && reliable) || (kind != ChallengeKind && !reliable)) return;
                if (kind == MessageKinds.InputBatch)
                {
                    hostInputFrameCount++;
                    if (hostInputFrameCount % 300 == 0)
                    {
                        BridgeContext.Info(string.Format("[t13] host input frames={0} lastSeq={1}", hostInputFrameCount, InputAdapter.LastAcceptedSeq(PlayerIds.P2)));
                    }
                }
                else if (kind == MessageKinds.Pong)
                {
                    hostPongCount++;
                    if (hostPongCount % 60 == 0)
                    {
                        BridgeContext.Info(string.Format("[t13] host pong count={0}", hostPongCount));
                    }
                }
                else
                {
                    BridgeContext.Info(string.Format("[t13] host frame kind={0} reliable={1}", kind, reliable));
                }

                switch (kind)
                {
                    case MessageKinds.Hello:
                        HandleHostHello(body);
                        break;
                    case ChallengeKind:
                        HandleHostChallenge(body);
                        break;
                    case MessageKinds.Pong:
                        PongMessage pong = PongMessage.Decode(body);
                        if (hostState == HostState.Active && session != null && pong.ConnectionGeneration == session.ConnectionGeneration)
                        {
                            session.SetPeerState(ConnectionState.Active);
                            session.TouchPeer(SessionModel.NowTicks());
                        }
                        break;
                    case MessageKinds.InputBatch:
                        HandleHostInput(body);
                        break;
                    case MessageKinds.SceneReady:
                        HandleHostSceneReady(SceneReadyMessage.Decode(body));
                        break;
                    case MessageKinds.SceneFailed:
                        SceneFailedMessage failed=SceneFailedMessage.Decode(body);
                        lastError="client scene not ready: "+failed.Detail;
                        BridgeContext.Info("[join] "+lastError);
                        OnHostDisconnected();
                        link.DisconnectPeer();
                        break;
                    case MessageKinds.BaselineAck:
                        if (pendingAdmission.IsExpired(SessionModel.NowTicks()))
                        {
                            BridgeContext.Info("[t22] dropped baseline acknowledgement after admission deadline");
                            break;
                        }
                        if (hostState == HostState.CatchingUp && joinTransaction != null && joinTransaction.OnBaselineAck(BaselineAckMessage.Decode(body)))
                        {
                            hostState = HostState.Active;
                            peerEverActive=true;
                            // T19: a (re)joined peer is attached again, so the party gate waits for it.
                            BridgeContext.PeerDetached = false;
                            pendingAdmission.Confirm();
                            session.SetPeerState(ConnectionState.Active);
                            session.TouchPeer(SessionModel.NowTicks());
                            BridgeContext.Info(string.Format("[t22] slot active generation={0} epoch={1} baseline={2}",
                                session.ConnectionGeneration, session.WorldEpoch, sceneTransitionId));
                        }
                        break;
                    default:
                        Fail("unknown message kind " + kind);
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail(ex.ToString());
            }
        }

        private void HandleHostHello(byte[] body)
        {
            HelloMessage hello = HelloMessage.Decode(body);
            if (hello.Protocol != ProtocolConstants.ProtocolVersion)
            {
                SendReject(RejectReason.VersionMismatch, "protocol version mismatch");
                return;
            }
            if (!BuildIdentity.IsCompatible(hello.Build, allowBuildMismatch))
            {
                // Same protocol number is not enough: both ends must run the same
                // bytes, or a snapshot added field silently mis-decodes.
                BridgeContext.Error(string.Format("[t13] build mismatch host={0} peer={1} allowMismatch={2}",
                    BuildIdentity.Current, string.IsNullOrEmpty(hello.Build) ? "<none>" : hello.Build, allowBuildMismatch));
                SendReject(RejectReason.VersionMismatch,
                    "build mismatch host=" + BuildIdentity.Current + " peer=" + (hello.Build == null ? "<none>" : hello.Build));
                return;
            }
            if (hello.RequestedSlot != PlayerIds.P2)
            {
                SendReject(RejectReason.SlotOccupied, "slot reserved");
                return;
            }
            if (!BytesEqual(hello.ContentDigest, ContentDigest()))
            {
                SendReject(RejectReason.BuildMismatch, "content digest mismatch");
                return;
            }
            uint now = SessionModel.NowTicks();
            bool resumed = reconnectWindow.State == SlotState.Grace;
            bool retryPending = pendingAdmission.IsPending;
            uint originalGraceStartedAt = 0;
            if (retryPending)
            {
                if (hostState != HostState.Listening || !reconnectWindow.CanResume(now) ||
                    !pendingAdmission.TryRetry(hello.ReconnectTicket, session.Id.ToArray(), session.WorldEpoch, session.ConnectionGeneration, now))
                {
                    SendReject(RejectReason.AuthFailed, "pending resume refused");
                    return;
                }
                if (!reconnectWindow.ResumePending(session.ConnectionGeneration, now))
                    throw new InvalidOperationException("pending resume window diverged");
            }
            else if (resumed)
            {
                originalGraceStartedAt = unchecked(now - (reconnectWindow.GraceMs - reconnectWindow.RemainingMs(now)));
                uint generation;
                ResumeResult result = ResumeGate.TryResume(reconnectWindow, reconnectTickets,
                    hello.ReconnectTicket, session.Id, now, out generation);
                if (result != ResumeResult.Resumed)
                {
                    SendReject(RejectReason.AuthFailed, "resume refused: " + result);
                    return;
                }
                if (session.NextConnectionGeneration() != generation)
                    throw new InvalidOperationException("resume generation diverged");
                // Any other rotating credentials from the old generation are now invalid.
                reconnectTickets.Clear();
            }
            else if (reconnectWindow.State == SlotState.Occupied || hostState != HostState.Listening)
            {
                SendReject(RejectReason.SlotOccupied, "slot occupied");
                return;
            }
            else
            {
                reconnectWindow.Occupy(now);
                if (session.NextConnectionGeneration() != reconnectWindow.Generation)
                    throw new InvalidOperationException("join generation diverged");
                pendingAdmission.BeginFresh(now, SceneLoadTimeoutMs);
            }

            challengeNonce = NewNonce();
            InputAdapter.ResetRemoteInput(PlayerIds.P2);
            waitingForHello = false;
            closeRejectedPeer = false;
            joinTransaction = null;
            session.SetPeerState(ConnectionState.Disconnected);
            session.TouchPeer(now);

            WelcomeMessage welcome = new WelcomeMessage();
            welcome.SessionId = session.Id.ToArray();
            welcome.Slot = PlayerIds.P2;
            welcome.ConnectionGeneration = session.ConnectionGeneration;
            welcome.CatalogDigest = new byte[16];
            welcome.TcpPort = 0;
            welcome.UdpPort = 0;
            welcome.ChallengeNonce = challengeNonce;
            welcome.WorldEpoch = session.WorldEpoch;
            welcome.ReconnectTicket = retryPending ? pendingAdmission.ReplacementTicket :
                reconnectTickets.Issue(PlayerIds.P2, session.Id, session.ConnectionGeneration, now);
            if (resumed && !retryPending)
                pendingAdmission.BeginResume(hello.ReconnectTicket, welcome.ReconnectTicket, session.Id.ToArray(), session.WorldEpoch,
                    session.ConnectionGeneration, originalGraceStartedAt, reconnectWindow.GraceMs);
            lastTicketTicks = now;
            SendReliable(MessageKinds.Welcome, welcome.Encode());
            BridgeContext.Info(string.Format("[t13] welcome sent resumed={0} session={1} generation={2} epoch={3}",
                resumed, session.Id, session.ConnectionGeneration, session.WorldEpoch));

            hostState = HostState.P2Joining;
            endpointBound=false;
            preparedScene=null;
            waitingSceneLogged=false;
            BridgeContext.Info(string.Format("[t13] P2 joined slot=2 gen={0}", session.ConnectionGeneration));
        }

        private void HandleHostChallenge(byte[] body)
        {
            if (hostState != HostState.P2Joining) return;
            ByteReader r = new ByteReader(body);
            byte[] sid = r.ReadBytes(16);
            byte[] nonce = r.ReadBytes(16);
            if (r.Remaining != 0)
            {
                throw new ProtocolException("challenge frame has trailing bytes");
            }
            bool sidOk = session != null && session.Id != null && BytesEqual(sid, session.Id.ToArray());
            bool nonceOk = challengeNonce != null && BytesEqual(nonce, challengeNonce);
            if (!sidOk || !nonceOk)
            {
                BridgeContext.Info("[t22] dropped stale or invalid UDP challenge");
                return;
            }

            // Transport readiness is not scene readiness. Do not publish a
            // baseline or enable gameplay input while either end is in menus.
            hostState = HostState.P2Loading;
            link.ConfirmUnreliablePeer();
            endpointBound=true;
            BridgeContext.Info("[t13] udp endpoint bound");
        }

        /// <summary>
        /// Join gate shared by both ends. Whitelisted scenes always pass; anything
        /// else needs the explicit non-strict legacy path. The decision is logged
        /// once per scene so a trace shows whether a run was audited or merely
        /// heuristic.
        /// </summary>
        private bool IsJoinScene(string scene)
        {
            bool allowed = SceneWhitelist.IsAllowed(sceneWhitelistCsv, sceneStrict, scene);
            if (!string.Equals(lastSceneDecisionLogged, scene, StringComparison.Ordinal))
            {
                lastSceneDecisionLogged = scene;
                BridgeContext.Info(string.Format("[join] scene gate scene={0} allowed={1} reason={2}",
                    string.IsNullOrEmpty(scene) ? "<empty>" : scene,
                    allowed,
                    SceneWhitelist.Describe(sceneWhitelistCsv, sceneStrict, scene)));
            }
            return allowed;
        }

        /// <summary>
        /// P2 must already own its native component set before the host asks a peer
        /// to load the scene; otherwise the peer is admitted into a world where the
        /// second actor exists but cannot act.
        /// </summary>
        private static bool P2BindingReady()
        {
            try
            {
                Actors.NativePlayerBinding binding = BridgeContext.EnsureBinding(PlayerIds.P2);
                return binding != null && binding.Root != null && binding.Abilities != null
                    && binding.Action != null && binding.Attribute != null && binding.StateMachine != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Client isolation sampling. Counts what must stay at zero on a client
        /// (locally spawned authorities) and what it is allowed to show (replica
        /// visuals), so a run can be judged from its trace instead of described.
        /// Observation only; it never mutates gameplay state.
        /// </summary>
        private void LogIsolationSample()
        {
            string scene = string.Empty;
            int enemies = -1;
            string hp = "?";
            try { scene = LevelManager.SceneName; } catch { }
            try
            {
                UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(typeof(EnemyBaseAction));
                enemies = found == null ? 0 : found.Length;
            }
            catch { }
            try
            {
                PlayerAttribute attribute = CameraHudAdapter.GetLocalPlayerAttribute();
                if (attribute != null)
                {
                    hp = attribute.currentHP.ToString();
                }
            }
            catch { }
            BridgeContext.Info(string.Format("[iso] client scene={0} localEnemies={1} p2Root={2} localHp={3} suppressedLocalEnemies={4} isolation={5}",
                scene, enemies, BridgeContext.P2Root != null, hp, ClientIsolation.SuppressedEnemies,
                ClientIsolation.Enabled ? "on" : "off"));
        }

        private void HandleHostSceneReady(SceneReadyMessage ready)
        {
            if(hostState!=HostState.P2Loading || preparedScene==null || ready.TransitionId!=sceneTransitionId ||
                ready.SceneKey!=preparedScene || LevelManager.SceneName!=preparedScene || !BytesEqual(ready.BindingDigest,ContentDigest()))
            {
                SendReject(RejectReason.AuthFailed,"scene readiness mismatch; join not activated");
                return;
            }
            joinTransaction=new JoinTransaction();
            joinTransaction.Begin(link,session.WorldEpoch,EntityAdapter.DumpEntities,hostEventSeq,hostTick,sceneTransitionId);
            hostState=HostState.CatchingUp;
            session.TouchPeer(SessionModel.NowTicks());
        }

        private void HandleHostInput(byte[] body)
        {
            if(hostState!=HostState.Active) return;
            InputBatchMessage msg = InputBatchMessage.Decode(body);
            if (msg.ConnectionGeneration != session.ConnectionGeneration || msg.WorldEpoch != session.WorldEpoch || msg.Batch.PlayerId != PlayerIds.P2)
            {
                BridgeContext.Info("[t22] rejected stale or wrong-slot input generation=" + msg.ConnectionGeneration + " epoch=" + msg.WorldEpoch);
                return;
            }
            session.TouchPeer(SessionModel.NowTicks());
            for (int i = 0; i < msg.Batch.Frames.Length; i++)
            {
                IceyCoop.Protocol.InputAckResult result = InputAdapter.ApplyRemoteInput(msg.Batch.PlayerId, msg.Batch.Frames[i]);
                InputAckMessage ack = new InputAckMessage();
                ack.PlayerId = msg.Batch.PlayerId;
                ack.AppliedInputSeq = msg.Batch.Frames[i].InputSeq;
                ack.Result = (byte)result;
                SendReliable(MessageKinds.InputAck, ack.Encode());
            }
        }

        // ---- client ----

        private void OnClientFrame(byte[] payload, bool reliable)
        {
            if (IsEnded) return;
            try
            {
                ushort kind = ReadKind(payload);
                byte[] body = ReadBody(payload);
                if (kind == MessageKinds.Ping)
                {
                    clientPingCount++;
                    if (clientPingCount % 60 == 0)
                    {
                        BridgeContext.Info(string.Format("[t13] client ping count={0}", clientPingCount));
                    }
                }
                else if (kind != MessageKinds.InputAck)
                {
                    BridgeContext.Info(string.Format("[t13] client frame kind={0} reliable={1}", kind, reliable));
                }

                switch (kind)
                {
                    case MessageKinds.Welcome:
                        HandleClientWelcome(body);
                        break;
                    case MessageKinds.ReconnectTicket:
                        ReconnectTicketMessage update = ReconnectTicketMessage.Decode(body);
                        if (update.ConnectionGeneration == clientConnectionGeneration && BytesEqual(update.SessionId, remoteSessionId))
                            SaveClientCredential(update.Ticket);
                        break;
                    case MessageKinds.ScenePrepare:
                        HandleClientScenePrepare(ScenePrepareMessage.Decode(body));
                        break;
                    case MessageKinds.Reject:
                        HandleClientReject(body);
                        break;
                    case MessageKinds.SessionEnded:
                        if (reliable) HandleClientSessionEnded(body);
                        break;
                    case MessageKinds.Ping:
                        PingMessage ping = PingMessage.Decode(body);
                        PongMessage pong = new PongMessage();
                        pong.Timestamp = ping.Timestamp;
                        pong.ConnectionGeneration = ping.ConnectionGeneration;
                        SendReliable(MessageKinds.Pong, pong.Encode());
                        break;
                    case MessageKinds.InputAck:
                        InputAckMessage ack = InputAckMessage.Decode(body);
                        if (ack.PlayerId != PlayerIds.P2)
                            throw new ProtocolException("input ack player must be P2");
                        bool acceptedAck = ack.Result == (byte)IceyCoop.Protocol.InputAckResult.Applied
                            && ack.AppliedInputSeq > 0
                            && ack.AppliedInputSeq <= clientInputSentSeq
                            && ack.AppliedInputSeq > clientInputAckedSeq;
                        if (acceptedAck)
                        {
                            clientInputAckedSeq = ack.AppliedInputSeq;
                            clientInputAckTicks = SessionModel.NowTicks();
                            hasClientInputAck = true;
                            clientAckCount++;
                        }
                        else
                        {
                            clientRejectedAckCount++;
                        }
                        if (acceptedAck && clientAckCount % 300 == 0)
                        {
                            BridgeContext.Info(string.Format("[t13] client ack count={0} valid=1 rejected={1}", clientAckCount, clientRejectedAckCount));
                        }
                        break;
                    case MessageKinds.BaselineBegin:
                        BaselineBeginMessage begin = BaselineBeginMessage.Decode(body);
                        if (clientJoin.State != JoinState.ApplyingBaseline || begin.Epoch != clientBaselineEpoch || begin.BaselineId != pendingBaselineId)
                            throw new ProtocolException("unsolicited or stale baseline");
                        clientBaselineId = begin.BaselineId;
                        clientBaselineHostTick = begin.HostTick;
                        clientBaselineIncludedSeq = begin.IncludedEventSeq;
                        clientBaselineEpoch = begin.Epoch;
                        ResetSnapshotFreshness(begin.Epoch);
                        clientBaselineData = new byte[0];
                        clientBaselineNextChunk = 0;
                        clientBaselineChunkCount = begin.ChunkCount;
                        clientBaselineLength = begin.TotalLength;
                        clientBaselineDigest = begin.Digest;
                        clientBaselineEntities.Clear();
                        break;
                    case MessageKinds.BaselineChunk:
                        BaselineChunkMessage chunk = BaselineChunkMessage.Decode(body);
                        if (clientJoin.State != JoinState.ApplyingBaseline || chunk.BaselineId != clientBaselineId || chunk.Index != clientBaselineNextChunk || clientBaselineNextChunk >= clientBaselineChunkCount)
                            throw new ProtocolException("baseline chunk order/id mismatch");
                        if (clientBaselineData == null || clientBaselineData.Length + chunk.Data.Length > clientBaselineLength)
                            throw new ProtocolException("baseline chunk exceeds declared length");
                        clientBaselineData = Append(clientBaselineData, chunk.Data);
                        clientBaselineEntities.AddRange(DecodeEntities(chunk.Data));
                        clientBaselineNextChunk++;
                        break;
                    case MessageKinds.BaselineEnd:
                        HandleClientBaselineEnd(body);
                        break;
                    case MessageKinds.WorldEvent:
                        HandleClientWorldEvent(body);
                        break;
                    case MessageKinds.StateSnapshot:
                        HandleClientSnapshot(body);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.ToString();
                BridgeContext.Error("[t13] client frame error: " + ex);
            }
        }

        private void HandleClientWelcome(byte[] body)
        {
            WelcomeMessage welcome = WelcomeMessage.Decode(body);
            if (clientJoin.State != JoinState.Handshaking) throw new ProtocolException("unsolicited Welcome");
            if (welcome.ReconnectTicket == null || welcome.ReconnectTicket.Length != ReconnectTickets.TicketLength)
                throw new ProtocolException("Welcome has no valid resume credential");
            remoteSessionId = welcome.SessionId;
            challengeNonce = welcome.ChallengeNonce;
            clientConnectionGeneration = welcome.ConnectionGeneration;
            clientBaselineEpoch = welcome.WorldEpoch;
            ResetSnapshotFreshness(welcome.WorldEpoch);
            SaveClientCredential(welcome.ReconnectTicket);

            clientJoin.HandshakeAccepted();

            SendChallenge();

            BridgeContext.Info(string.Format("[t13] client joined slot={0} gen={1}", welcome.Slot, welcome.ConnectionGeneration));
        }

        /// <summary>
        /// Scene preparation entry on the client.
        ///
        /// Same scene: answer immediately. Different scene: keep the peer Loading
        /// and drive the native loader (LevelManager.LoadScene, which is the game's
        /// own async path) until the active scene matches, then answer. Anything
        /// that cannot be aligned fails closed with an explicit reason - the peer is
        /// never admitted into a world it did not load.
        /// </summary>
        private void HandleClientScenePrepare(ScenePrepareMessage prepare)
        {
            clientBaselineEpoch = prepare.NextEpoch;
            ResetSnapshotFreshness(prepare.NextEpoch);
            pendingBaselineId = prepare.TransitionId;
            if(clientJoin.State!=JoinState.Loading)
            {
                // Mid-session transition (T19): the host moved rooms while we were Active.
                if(clientJoin.State==JoinState.Active || clientJoin.State==JoinState.CatchingUp)
                {
                    clientJoin.BeginTransition();
                    BridgeContext.Info("[t19] client accepted a mid-session scene transition");
                }
                else
                {
                    throw new ProtocolException("scene preparation outside Loading");
                }
            }
            string current=LevelManager.SceneName;
            if(!IsJoinScene(prepare.SceneKey))
            {
                FailScene(prepare.TransitionId, prepare.SceneKey, current, SceneFailedReason.SceneMismatch,
                    "scene refused by join gate");
                return;
            }
            if(current==prepare.SceneKey)
            {
                pendingSceneKey=null;
                pendingTransitionId=0;
                sceneLoadInFlight=false;
                clientJoin.Loaded();
                SceneReadyMessage ready=new SceneReadyMessage();
                ready.TransitionId=prepare.TransitionId;
                ready.SceneKey=current;
                ready.BindingDigest=ContentDigest();
                SendReliable(MessageKinds.SceneReady,ready.Encode());
                BridgeContext.Info("[join] client already in scene; SceneReady sent scene="+current);
                return;
            }
            if(disableAutoScene)
            {
                FailScene(prepare.TransitionId, prepare.SceneKey, current, SceneFailedReason.SceneMismatch,
                    "automatic scene integration disabled by ICEYCOOP_NO_AUTO_SCENE=1");
                return;
            }

            pendingSceneKey=prepare.SceneKey;
            pendingTransitionId=prepare.TransitionId;
            sceneLoadInFlight=true;
            sceneReadySettleFrames=0;
            sceneLoadDeadlineTicks=SessionModel.NowTicks()+SceneLoadTimeoutMs;
            BridgeContext.Info(string.Format("[join] client auto-loading scene={0} was={1}", prepare.SceneKey, current));
            try
            {
                IceyCoop.GameBridge.World.SceneLoadGate.LoadAuthorized(prepare.SceneKey);
            }
            catch(Exception ex)
            {
                sceneLoadInFlight=false;
                FailScene(prepare.TransitionId, prepare.SceneKey, current, SceneFailedReason.LoadFailed,
                    "scene load threw: "+ex.Message);
            }
        }

        /// <summary>
        /// Drives the auto-load transaction. Answers SceneReady only after the
        /// active scene matched for a few frames, so GameSceneInit and the native
        /// bindings have run; otherwise the peer would be activated into a
        /// half-initialised world.
        /// </summary>
        /// <summary>
        /// T19: the host owns scene changes. When the authoritative world finishes loading
        /// another room while a peer is connected, the epoch is bumped and the same
        /// ScenePrepare/SceneReady handshake used for joining is re-run, so the peer loads
        /// the new room and receives a fresh baseline for the new epoch instead of keeping
        /// a half-world from the previous one.
        /// </summary>
        private void PollHostSceneChange()
        {
            if (role != Role.Host) return;
            string scene = LevelManager.SceneName;
            if (string.IsNullOrEmpty(scene)) return;
            if (lastHostScene == null)
            {
                lastHostScene = scene;
                return;
            }
            if (string.Equals(scene, lastHostScene, StringComparison.Ordinal)) return;
            string previous = lastHostScene;
            lastHostScene = scene;
            if (hostState != HostState.Active || !endpointBound) return;
            if (!IsJoinScene(scene))
            {
                BridgeContext.Info("[t19] host scene changed to a non-gameplay scene; peer stays as is scene=" + scene);
                return;
            }
            uint epoch = session.NextWorldEpoch();
            hostState = HostState.P2Loading;
            preparedScene = null;
            pendingAdmission.BeginFresh(SessionModel.NowTicks(), SceneLoadTimeoutMs);
            waitingSceneLogged = false;
            // Record what the transition carries over: the table itself is unit tested
            // (SceneMigration), here we only report how many entities the host is moving.
            try
            {
                System.Collections.Generic.List<IceyCoop.Protocol.EntitySource> src = EntityAdapter.DumpEntities();
                int n = src == null ? 0 : src.Count;
                BridgeContext.Info(string.Format("[t19] migration entities={0} (players kept, rewards carried, room entities despawned)", n));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t19] migration report failed: " + ex.Message);
            }            BridgeContext.Info(string.Format("[t19] host scene transition {0} -> {1} epoch={2}", previous, scene, epoch));
        }
        private void PollSceneLoad()
        {
            string current=LevelManager.SceneName;
            if(current==pendingSceneKey)
            {
                sceneReadySettleFrames++;
                if(sceneReadySettleFrames<SceneReadySettleFrames) return;
                sceneLoadInFlight=false;
                clientJoin.Loaded();
                SceneReadyMessage ready=new SceneReadyMessage();
                ready.TransitionId=pendingTransitionId;
                ready.SceneKey=current;
                ready.BindingDigest=ContentDigest();
                SendReliable(MessageKinds.SceneReady,ready.Encode());
                BridgeContext.Info("[join] client scene aligned after load; SceneReady sent scene="+current);
                pendingSceneKey=null;
                pendingTransitionId=0;
                return;
            }
            long now=SessionModel.NowTicks();
            // The title boot sequence loads splash → ui_start and can override a load
            // issued during splash (observed: client stuck on ui_start, host in the room).
            // Keep re-issuing until the requested scene actually becomes active.
            if(now>sceneLoadRetryTicks && now<=sceneLoadDeadlineTicks)
            {
                sceneLoadRetryTicks=now+SceneLoadRetryMs;
                BridgeContext.Info("[join] re-issuing scene load target="+pendingSceneKey+" current="+current);
                try
                {
                    IceyCoop.GameBridge.World.SceneLoadGate.LoadAuthorized(pendingSceneKey);
                }
                catch(Exception ex)
                {
                    BridgeContext.Error("[join] scene load retry threw: "+ex.Message);
                }
                return;
            }
            if(now>sceneLoadDeadlineTicks)
            {
                sceneLoadInFlight=false;
                FailScene(pendingTransitionId, pendingSceneKey, current, SceneFailedReason.Timeout,
                    "scene load timed out");
                pendingSceneKey=null;
                pendingTransitionId=0;
            }
        }

        private void FailScene(uint transitionId, string targetScene, string currentScene,
            SceneFailedReason reason, string why)
        {
            SceneFailedMessage failed=new SceneFailedMessage();
            failed.TransitionId=transitionId;
            failed.Reason=(byte)reason;
            failed.Detail=why+"; client scene="+(string.IsNullOrEmpty(currentScene)?"<none>":currentScene)
                +", host="+(string.IsNullOrEmpty(targetScene)?"<none>":targetScene);
            SendReliable(MessageKinds.SceneFailed,failed.Encode());
            lastError=failed.Detail;
            clientJoin.LoadFailed();
            BridgeContext.Info("[join] "+failed.Detail+"; input remains disabled");
        }

        private void HandleClientBaselineEnd(byte[] body)
        {
            BaselineEndMessage end = BaselineEndMessage.Decode(body);
            if(clientJoin.State!=JoinState.ApplyingBaseline) throw new ProtocolException("baseline received before verified gameplay scene");
            if (end.BaselineId != clientBaselineId || end.TotalLength != clientBaselineLength || clientBaselineData == null ||
                clientBaselineData.Length != clientBaselineLength || clientBaselineNextChunk != clientBaselineChunkCount ||
                !BytesEqual(end.Digest, clientBaselineDigest) || !BytesEqual(BaselineDigest(clientBaselineData), end.Digest))
                throw new ProtocolException("baseline integrity mismatch");
            EntitySnapshot[] snaps = clientBaselineEntities.ToArray();
            IceyCoop.Protocol.EntityRecord[] records = new IceyCoop.Protocol.EntityRecord[snaps.Length];
            for (int i = 0; i < snaps.Length; i++)
            {
                records[i] = new IceyCoop.Protocol.EntityRecord(snaps[i].EntityId, snaps[i].Revision, snaps[i].Kind, false);
            }

            // T19 migration table: anything the new baseline does not contain belonged to
            // the previous room, so its presenter is dropped before the new baseline lands.
            try
            {
                System.Collections.Generic.List<ulong> newIds = new System.Collections.Generic.List<ulong>();
                for (int i = 0; i < records.Length; i++) newIds.Add(records[i].EntityId);
                ulong[] oldEnemies = ReplicaFactory.ActiveEnemyIds();
                int dropped = 0;
                for (int i = 0; i < oldEnemies.Length; i++)
                {
                    if (!newIds.Contains(oldEnemies[i])) { ReplicaFactory.RemoveEnemy(oldEnemies[i]); dropped++; }
                }
                ReplicaFactory.MigrationRemoved += dropped;
                BridgeContext.Info(string.Format("[t19] migration applied on client: baseline={0} stale_removed={1}", records.Length, dropped));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t19] client migration failed: " + ex.Message);
            }
            ApplyResult result = replicaStore.ApplyBaseline(clientBaselineEpoch, clientBaselineIncludedSeq, records);
            if (result != ApplyResult.Applied)
            {
                BridgeContext.Error("[t15] baseline apply failed: " + result);
                SendReject(RejectReason.AuthFailed, "baseline apply failed");
                return;
            }
            BridgeContext.Info(string.Format("[t15] baseline applied B={0} entities={1}", clientBaselineIncludedSeq, snaps.Length));
            snapshotFreshness.BeginBaseline(clientBaselineEpoch, clientBaselineHostTick, clientBaselineIncludedSeq);

            // G7: bring the joiner into the host's current story state (control locks,
            // world time) so a mid-sequence join does not leave it input-enabled in a cutscene.
            if (end.StorySnapshot != null && end.StorySnapshot.Length > 0)
            {
                try
                {
                    StoryCapture.ApplyRemote(StorySnapshotCodec.Decode(end.StorySnapshot));
                    BridgeContext.Info("[g7] baseline story state applied");
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[g7] baseline story snapshot decode failed: " + ex.Message);
                }
            }

            // Build visual presenters for every baseline entity so subsequent
            // snapshots hit ApplyRecord instead of "unknown entity waiting spawn".
            for (int i = 0; i < snaps.Length; i++)
            {
                EntitySnapshot snap = snaps[i];
                if (snap.EntityId == CombatRouter.EntityIdP1 || snap.EntityId == CombatRouter.EntityIdP2)
                {
                    byte playerId = snap.EntityId == CombatRouter.EntityIdP2 ? PlayerIds.P2 : PlayerIds.P1;
                    ReplicaFactory.SpawnPlayer(playerId, snap);
                }
                else
                {
                    ReplicaFactory.SpawnEnemy(snap);
                }
            }

            BaselineAckMessage ack = new BaselineAckMessage();
            ack.BaselineId = end.BaselineId;
            ack.IncludedEventSeq = clientBaselineIncludedSeq;
            ack.AppliedDigest = end.Digest;
            SendReliable(MessageKinds.BaselineAck, ack.Encode());

            clientJoin.BaselineApplied();
            clientJoin.CaughtUp();
        }

        private void HandleClientWorldEvent(byte[] body)
        {
            WorldEventMessage ev = WorldEventMessage.Decode(body);
            // Only some subtypes carry an EntitySnapshot payload. HitResolved, EncounterState
            // and the cue messages use their own formats, and decoding those as a snapshot
            // threw "truncated frame" - which dropped the event, produced a sequence gap and
            // lost the client's enemy replicas entirely.
            bool snapshotPayload = ev.SubType == (byte)WorldEventSubType.EntitySpawn
                || ev.SubType == (byte)WorldEventSubType.EntityDespawn;
            EntitySnapshot snap = snapshotPayload && ev.Payload != null && ev.Payload.Length > 0
                ? EntitySnapshotCodec.Decode(ev.Payload)
                : new EntitySnapshot();
            if (!snapshotPayload) snap.Kind = -1;
            // Non-entity events (EncounterState / ObjectState / CameraCue / ControlLock /
            // WorldTime) share the entityId=0 sentinel record. EncounterState's wire
            // revision carries its generation (>=1), which made every later ObjectState
            // (revision 0) look like a revision rollback and broke the event seq chain
            // (GapEvent from there on, dropping the spawns that followed). Sentinel
            // records carry no revision semantics. Entity-addressed events retain
            // their known kind/revision so snapshots cannot break the event cursor.
            IceyCoop.Protocol.EntityRecord record = RuntimeReplicaPolicy.CreateEventRecord(replicaStore, ev, snap, snapshotPayload);
            ApplyResult r = replicaStore.ApplyEvent(clientBaselineEpoch, ev.EventSeq, record);
            if (r != ApplyResult.Applied)
            {
                BridgeContext.Error(string.Format("[t15] event apply failed: {0} seq={1}", r, ev.EventSeq));
                return;
            }
            ReplicaFactory.ApplyWorldEvent(ev);
            BridgeContext.Info(string.Format("[t15] event applied kind={0} entity={1}", ev.SubType, ev.TargetEntityId));
        }

        private void HandleClientSnapshot(byte[] body)
        {
            if (clientJoin.State != JoinState.Active) return;
            StateSnapshotMessage msg = StateSnapshotMessage.Decode(body);
            if (!RuntimeReplicaPolicy.CanPresentSnapshot(replicaStore, msg))
            {
                PendingSnapshotEvents = msg.IncludedEventSeq > replicaStore.AppliedEventSeq
                    ? msg.IncludedEventSeq - replicaStore.AppliedEventSeq : 0;
                return;
            }
            PendingSnapshotEvents = 0;
            if (!snapshotFreshness.TryAcceptPart(msg)) return;
            for (int i = 0; i < msg.Entities.Length; i++)
            {
                EntitySnapshot snap = msg.Entities[i];
                IceyCoop.Protocol.EntityRecord record = new IceyCoop.Protocol.EntityRecord(snap.EntityId, snap.Revision, snap.Kind, false);
                if (replicaStore.ApplySnapshot(clientBaselineEpoch, msg.IncludedEventSeq,
                    new IceyCoop.Protocol.EntityRecord[] { record }) == ApplyResult.Applied)
                    ReplicaFactory.ApplyRecord(snap);
            }
            if (!telemetryEpochSet || telemetryEpoch != clientBaselineEpoch)
            {
                ResetSnapshotFreshness(clientBaselineEpoch);
            }
            clientSnapshotTicks = SessionModel.NowTicks();
            hasClientSnapshot = true;
            clientSnapshotsReceived++;
        }

        private void ResetSnapshotFreshness(uint epoch)
        {
            telemetryEpoch = epoch;
            telemetryEpochSet = true;
            clientSnapshotTicks = 0;
            hasClientSnapshot = false;
            clientSnapshotsReceived = 0;
            clientInputSentSeq = 0;
            clientInputSentTicks = 0;
            clientInputAckedSeq = 0;
            hasClientInputAck = false;
            telemetryWasActive = false;
            snapshotFreshness.Reset();
        }

        private static EntitySnapshot[] DecodeEntities(byte[] data)
        {
            ByteReader r = new ByteReader(data == null ? new byte[0] : data);
            uint count = r.ReadUInt32();
            if (count > 4096)
            {
                throw new ProtocolException("baseline entity count exceeds limit");
            }
            EntitySnapshot[] snaps = new EntitySnapshot[count];
            for (uint i = 0; i < count; i++)
            {
                ushort len = r.ReadUInt16();
                byte[] bytes = r.ReadBytes(len);
                snaps[i] = EntitySnapshotCodec.Decode(bytes);
            }
            if (r.Remaining != 0)
            {
                throw new ProtocolException("baseline data has trailing bytes");
            }
            return snaps;
        }

        private static byte[] BaselineDigest(byte[] data)
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] full = sha.ComputeHash(data);
                byte[] digest = new byte[16];
                Array.Copy(full, digest, 16);
                return digest;
            }
        }

        private static byte[] Append(byte[] a, byte[] b)
        {
            byte[] merged = new byte[(a == null ? 0 : a.Length) + (b == null ? 0 : b.Length)];
            if (a != null)
            {
                Array.Copy(a, 0, merged, 0, a.Length);
            }
            if (b != null)
            {
                Array.Copy(b, 0, merged, a == null ? 0 : a.Length, b.Length);
            }
            return merged;
        }

        private void HandleClientReject(byte[] body)
        {
            RejectMessage reject = RejectMessage.Decode(body);
            lastError = reject.Detail;
            if(clientJoin.State==JoinState.Handshaking) clientJoin.HandshakeRejected();
            else if(clientJoin.State==JoinState.Loading) clientJoin.LoadFailed();
            else clientJoin.Disconnect();
            BridgeContext.Error(string.Format("[t13] rejected reason={0}: {1}", reject.Reason, reject.Detail));
        }

        private void SendHello()
        {
            HelloMessage hello = new HelloMessage();
            hello.Protocol = ProtocolConstants.ProtocolVersion;
            hello.Mod = "iceycoop";
            hello.Build = BuildIdentity.Current;
            hello.ContentDigest = ContentDigest();
            hello.RequestedSlot = PlayerIds.P2;
            hello.ReconnectTicket = clientReconnectTicket;
            hello.Capabilities = 0;
            SendReliable(MessageKinds.Hello, hello.Encode());
            BridgeContext.Info("[t13] hello sent");
        }

        private void SendChallenge()
        {
            ByteWriter w = new ByteWriter();
            w.WriteBytes(remoteSessionId); w.WriteBytes(challengeNonce);
            SendUnreliable(ChallengeKind, w.ToArray());
            lastChallengeTicks = SessionModel.NowTicks();
        }

        private void SendInputFrame(InputFrame frame)
        {
            if (link == null || !link.IsConnected || clientJoin.State != JoinState.Active)
                return;
            InputBatchMessage msg = new InputBatchMessage();
            msg.ConnectionGeneration = clientConnectionGeneration;
            msg.WorldEpoch = clientBaselineEpoch;
            msg.Batch = new InputBatch();
            msg.Batch.PlayerId = PlayerIds.P2;
            msg.Batch.Frames = new InputFrame[] { frame };
            SendReliable(MessageKinds.InputBatch, msg.Encode());
            clientInputSentSeq = frame.InputSeq;
            clientInputSentTicks = SessionModel.NowTicks();
            BridgeContext.Info(string.Format("[t13] input sent seq={0}", frame.InputSeq));
            if (frame.Edges != null && frame.Edges.Length > 0)
            {
                for (int i = 0; i < frame.Edges.Length; i++)
                {
                    BridgeContext.Info(string.Format("[t17] input edges sent action={0} pressed={1} ordinal={2} seq={3}",
                        InputActions.NameOf(frame.Edges[i].Action),
                        frame.Edges[i].Pressed ? 1 : 0,
                        frame.Edges[i].Ordinal,
                        frame.InputSeq));
                }
            }
        }

        private void SendReject(RejectReason reason, string detail)
        {
            RejectMessage reject = new RejectMessage();
            reject.Reason = (byte)reason;
            reject.Detail = detail;
            SendReliable(MessageKinds.Reject, reject.Encode());
            lastError = detail;
            BridgeContext.Error("[t13] reject: " + detail);
            if (role == Role.Host)
            {
                closeRejectedPeer = true;
                closeRejectedAt = SessionModel.NowTicks();
            }
        }

        private void SendReliable(ushort kind, byte[] body)
        {
            if (link == null || !link.IsConnected)
            {
                return;
            }
            link.SendReliable(Frame(kind, body));
        }

        private void SendUnreliable(ushort kind, byte[] body)
        {
            if (link == null || !link.IsConnected)
            {
                return;
            }
            link.SendUnreliable(Frame(kind, body));
        }

        private void Fail(string message)
        {
            lastError = message;
            BridgeContext.Error("[t13] protocol error: " + message);
        }

        private void OnLinkDiagnostic(string message)
        {
            BridgeContext.Info("[t13] link: " + message);
        }

        private void OnHostWorldEvent(int subType, ulong entityId, ulong revision, byte[] payload)
        {
            if (IsEnded) return;
            hostEventSeq++;
            hostTick++;
            WorldEventMessage ev = new WorldEventMessage();
            ev.EventSeq = hostEventSeq;
            ev.HostTick = hostTick;
            ev.SubType = (byte)subType;
            ev.TargetEntityId = entityId;
            ev.EntityRevision = revision;
            ev.Payload = payload != null ? payload : new byte[0];

            if (joinTransaction != null &&
                (joinTransaction.CurrentState == JoinTransaction.State.AwaitingAck ||
                 joinTransaction.CurrentState == JoinTransaction.State.SendingBaseline))
            {
                joinTransaction.BufferEvent(ev);
            }
            else if (hostState == HostState.Active)
            {
                SendReliable(MessageKinds.WorldEvent, ev.Encode());
            }
        }

        /// <summary>Host sampling point: 50ms cadence driven by ProbeBridge.
        /// Splits the snapshot into UDP-sized parts and sends unreliably.</summary>
        public void SendSnapshot()
        {
            if (IsEnded || role != Role.Host || hostState != HostState.Active || link == null || !link.IsConnected)
            {
                return;
            }
            // Execution locks are only released when the state machine reports a transfer;
            // an execution that never started leaves the enemy locked, so sweep here (the
            // host tick) rather than waiting for the next attempt.
            T09ExecutionPatches.Watchdog();
            hostTick++;
            snapshotId++;
            SnapshotAssembler assembler = new SnapshotAssembler();
            assembler.IncludedEventSeq = hostEventSeq;
            assembler.HostTick = hostTick;
            List<EntitySnapshot> snaps = assembler.Build(EntityAdapter.DumpEntities());
            List<EntitySnapshot[]> parts = assembler.Slice(snaps, SnapshotAssembler.UdpSnapshotMaxBytes - 64);
            for (int i = 0; i < parts.Count; i++)
            {
                StateSnapshotMessage msg = new StateSnapshotMessage();
                msg.SnapshotId = snapshotId;
                msg.HostTick = hostTick;
                msg.IncludedEventSeq = hostEventSeq;
                msg.PartIndex = (ushort)i;
                msg.PartCount = (ushort)parts.Count;
                msg.Entities = parts[i];
                SendUnreliable(MessageKinds.StateSnapshot, msg.Encode());
            }
        }

        // ---- helpers ----

        private static byte[] Frame(ushort kind, byte[] body)
        {
            byte[] frame = new byte[2 + body.Length];
            frame[0] = (byte)(kind & 0xFF);
            frame[1] = (byte)((kind >> 8) & 0xFF);
            Array.Copy(body, 0, frame, 2, body.Length);
            return frame;
        }

        private static ushort ReadKind(byte[] frame)
        {
            if (frame == null || frame.Length < 2)
            {
                throw new ProtocolException("frame too short");
            }
            return (ushort)(frame[0] | (frame[1] << 8));
        }

        private static byte[] ReadBody(byte[] frame)
        {
            byte[] body = new byte[frame.Length - 2];
            Array.Copy(frame, 2, body, 0, body.Length);
            return body;
        }

        private byte[] ContentDigest()
        {
            // Assembly-CSharp SHA256 first 16 bytes, per the join contract.
            string root = string.IsNullOrEmpty(gameRoot) ? "." : gameRoot;
            string managed = System.IO.Path.Combine(
                System.IO.Path.Combine(System.IO.Path.Combine(root, "ICEY_Data"), "Managed"),
                "Assembly-CSharp.dll");
            string hex = IceyCoop.GameBridge.BindingManifest.ComputeSha256(managed);
            byte[] bytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        private static byte[] NewSessionId()
        {
            byte[] bytes = new byte[16];
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            rng.GetBytes(bytes);
            return bytes;
        }

        private static byte[] NewNonce()
        {
            byte[] bytes = new byte[16];
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            rng.GetBytes(bytes);
            return bytes;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
