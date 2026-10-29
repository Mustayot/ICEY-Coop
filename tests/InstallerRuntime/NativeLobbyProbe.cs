using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

[BepInPlugin("io.iceycoop.nativelobbyprobe", "ICEYCoop Native Lobby Probe", "0.1.0")]
public sealed class NativeLobbyProbe : BaseUnityPlugin
{
    private enum HostPhase
    {
        FindTitle, RunNegativeCases, WaitManualCreate, WaitHostGame, WaitClientReady,
        WaitClientDone, ReturnHostToTitle, RecreateRoom, WaitRecreatedGame,
        CloseRecreatedRoom, WaitFinalTitle, Finished
    }

    private enum ClientPhase
    {
        WaitHostReady, FindTitle, RunNegativeCases, WaitUnavailableFailure, WaitManualJoin, JoinRoom,
        WaitJoined, WaitHostAcknowledgement, CloseClientRoom, WaitClientTitle, WaitHostComplete, Finished
    }

    private string role;
    private string output;
    private string signals;
    private string connect;
    private int port;
    private int refusedPort;
    private bool manual;
    private bool installerProbe;
    private bool portableBootChecked;
    private float timeoutSeconds;
    private float started;
    private float actionStarted;
    private float hostNativeStartAt;
    private bool lobbyOpenPending;
    private string deferredLobbyPage;
    private string deferredLobbyAddress;
    private string deferredLobbyPort;
    private string expectedScene;
    private bool finished;
    private bool hostReadyWritten;
    private bool clientReadyWritten;
    private bool clientDoneWritten;
    private bool hostCompleteWritten;
    private bool menuCaptured;
    private bool panelCaptured;
    private int assertions;
    private int failures;
    private HostPhase hostPhase;
    private ClientPhase clientPhase;
    private TcpListener occupiedPort;
    private object plugin;
    private object bridge;
    private object lobbyActions;
    private Type lobbyActionsType;
    private readonly StringBuilder detail = new StringBuilder();

    private void Awake()
    {
        role = Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_ROLE");
        output = Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_OUTPUT");
        signals = Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_SIGNALS");
        connect = Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_CONNECT");
        manual = Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_MANUAL") == "1";
        installerProbe = Environment.GetEnvironmentVariable("ICEYCOOP_INSTALLER_PROBE") == "1";
        int parsed;
        if (!int.TryParse(Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_PORT"), out parsed)) parsed = 57820;
        port = parsed;
        if (!int.TryParse(Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_REFUSED_PORT"), out parsed)) parsed = port == 65535 ? port - 1 : port + 1;
        refusedPort = parsed;
        if (!float.TryParse(Environment.GetEnvironmentVariable("ICEYCOOP_LOBBY_PROBE_TIMEOUT"), out timeoutSeconds)) timeoutSeconds = 360f;
        if (timeoutSeconds < 60f) timeoutSeconds = 60f;

        if ((role != "host" && role != "client") || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(signals))
        {
            enabled = false;
            return;
        }

        started = Time.realtimeSinceStartup;
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        Log("MODE=" + (manual ? "manual" : "automatic") + " ROLE=" + role + " PORT=" + port + " CONNECT=" + connect + " INSTALLER_PROBE=" + installerProbe);
    }

    private void Update()
    {
        if (finished) return;
        try
        {
            if (!FindBridge()) return;
            if (Time.realtimeSinceStartup - started > timeoutSeconds)
            {
                FailAndFinish("timeout role=" + role + " scene=" + SceneName() + " phase=" + CurrentPhase());
                return;
            }

            if (lobbyOpenPending)
            {
                OpenLobbyScreen(deferredLobbyPage, deferredLobbyAddress, deferredLobbyPort);
                CaptureScreenshot(role == "host" ? "host-lobby-open.png" : "client-lobby-open.png");
                lobbyOpenPending = false;
                menuCaptured = true;
                panelCaptured = true;
                if (role == "host") hostPhase = HostPhase.RunNegativeCases;
                else clientPhase = ClientPhase.RunNegativeCases;
                return;
            }

            if (role == "host") UpdateHost();
            else UpdateClient();
        }
        catch (Exception ex)
        {
            FailAndFinish("probe exception: " + ex);
        }
    }

    private void UpdateHost()
    {
        switch (hostPhase)
        {
            case HostPhase.FindTitle:
                if (!IsTitle()) return;
                CaptureScreenshot("host-main-menu.png");
                ScheduleLobbyOpen("Create", string.Empty, port.ToString());
                break;

            case HostPhase.RunNegativeCases:
                RunStartupGateNegativeCases();
                RunInvalidPortCheck();
                RunOccupiedPortCheck();
                SetLobbyScreen("Create", string.Empty, port.ToString());
                if (manual)
                {
                    WriteSignal("host-ui-ready", "title lobby visible; click Create -> 创建并进入游戏");
                    hostPhase = HostPhase.WaitManualCreate;
                }
                else
                {
                    hostNativeStartAt = Time.realtimeSinceStartup;
                    LogHostStartUiEvidence("before-valid-CreateRoom port=" + port);
                    InvokeCreate(port);
                    hostPhase = HostPhase.WaitHostGame;
                }
                break;

            case HostPhase.WaitManualCreate:
                object manualStatus = ReadStatus();
                if (ReadBool(manualStatus, "IsListening") || ReadBool(manualStatus, "IsHost"))
                {
                    if (hostNativeStartAt <= 0f) hostNativeStartAt = Time.realtimeSinceStartup;
                    hostPhase = HostPhase.WaitHostGame;
                }
                break;

            case HostPhase.WaitHostGame:
                object startStatus = ReadStatus();
                if (ReadBool(startStatus, "IsListening") && IsGameplayScene())
                {
                    Assert(ReadBool(startStatus, "IsHost"), "host role installed after UI create");
                    Assert(ReadBool(startStatus, "IsListening"), "host TCP listener active");
                    Assert(ReadInt(startStatus, "Port") == port, "host reports requested port");
                    expectedScene = SceneName();
                    CaptureScreenshot("host-listening.png");
                    WriteSignal("host-ready", expectedScene);
                    hostReadyWritten = true;
                    Log("HOST_READY scene=" + expectedScene);
                    hostPhase = HostPhase.WaitClientReady;
                }
                else if (hostNativeStartAt > 0f && IsTitle()
                    && Time.realtimeSinceStartup - hostNativeStartAt >= 25f)
                {
                    LogHostStartStallDiagnostics();
                    FailAndFinish("native start remained in ui_start for 25 seconds after CreateRoom; inspect HostStartStall diagnostics");
                }
                break;

            case HostPhase.WaitClientReady:
                if (SignalExists("client-ready"))
                {
                    object currentMonitor = GetField(bridge, "healthMonitor");
                    object currentSample = GetProperty(currentMonitor, "CurrentSample");
                    if (currentSample == null || !ReadBool(currentSample, "Active")
                        || Convert.ToUInt64(ReadMember(currentSample, "InputAccepted")) == 0) break;
                    object status = ReadStatus();
                    Assert(ReadBool(status, "Connected"), "host sees active client before client-ready");
                    VerifyHealthReport("Host");
                    WriteSignal("host-ack-client-ready", "host verified active peer");
                    hostPhase = HostPhase.WaitClientDone;
                }
                break;

            case HostPhase.WaitClientDone:
                if (SignalExists("client-done"))
                {
                    InvokeClose();
                    hostPhase = HostPhase.ReturnHostToTitle;
                }
                break;

            case HostPhase.ReturnHostToTitle:
                if (IsTitle() && IsOffline() && !HasP2Root())
                {
                    Assert(true, "host returned to title offline and released P2");
                    hostPhase = HostPhase.RecreateRoom;
                }
                break;

            case HostPhase.RecreateRoom:
                if (!ReadBool(ReadStatus(), "CanStart")) return;
                LogHostStartUiEvidence("before-valid-recreate-CreateRoom port=" + port);
                InvokeCreate(port);
                hostPhase = HostPhase.WaitRecreatedGame;
                break;

            case HostPhase.WaitRecreatedGame:
                if (ReadBool(ReadStatus(), "IsListening") && IsGameplayScene())
                {
                    Assert(ReadBool(ReadStatus(), "IsHost"), "host can create a second room after shutdown");
                    InvokeClose();
                    hostPhase = HostPhase.WaitFinalTitle;
                }
                break;

            case HostPhase.WaitFinalTitle:
                if (IsTitle() && IsOffline() && !HasP2Root())
                {
                    Assert(true, "recreated host room closed cleanly back to single player");
                    WriteSignal("host-complete", "reusable host cycle passed");
                    hostCompleteWritten = true;
                    Finish();
                }
                break;
        }
    }

    private void UpdateClient()
    {
        switch (clientPhase)
        {
            case ClientPhase.WaitHostReady:
                if (SignalExists("host-ready"))
                {
                    expectedScene = ReadSignal("host-ready");
                    clientPhase = ClientPhase.FindTitle;
                }
                break;

            case ClientPhase.FindTitle:
                if (!IsTitle()) return;
                CaptureScreenshot("client-main-menu.png");
                ScheduleLobbyOpen("Join", connect, port.ToString());
                break;

            case ClientPhase.RunNegativeCases:
                RunInvalidAddressCheck();
                RunUnavailableAddressCheck();
                SetLobbyScreen("Join", connect, port.ToString());
                clientPhase = ClientPhase.WaitUnavailableFailure;
                break;

            case ClientPhase.WaitUnavailableFailure:
                CheckUnavailableJoinFailure();
                break;

            case ClientPhase.WaitManualJoin:
                if (ReadBool(ReadStatus(), "IsClient"))
                {
                    actionStarted = Time.realtimeSinceStartup;
                    clientPhase = ClientPhase.WaitJoined;
                }
                break;

            case ClientPhase.JoinRoom:
                if (!ReadBool(ReadStatus(), "CanStart")) return;
                actionStarted = Time.realtimeSinceStartup;
                InvokeJoin(connect);
                Assert(Time.realtimeSinceStartup - actionStarted < 0.5f, "JoinRoom returned without blocking the main thread");
                clientPhase = ClientPhase.WaitJoined;
                break;

            case ClientPhase.WaitJoined:
                CheckClientJoined();
                break;

            case ClientPhase.WaitHostAcknowledgement:
                if (SignalExists("host-ack-client-ready")) clientPhase = ClientPhase.CloseClientRoom;
                break;

            case ClientPhase.CloseClientRoom:
                InvokeClose();
                clientPhase = ClientPhase.WaitClientTitle;
                break;

            case ClientPhase.WaitClientTitle:
                if (IsTitle() && IsOffline() && !HasP2Root())
                {
                    Assert(true, "client returned to title offline with no P2 replica");
                    WriteSignal("client-done", "client closed room and released remote P2");
                    clientDoneWritten = true;
                    clientPhase = ClientPhase.WaitHostComplete;
                }
                break;

            case ClientPhase.WaitHostComplete:
                if (SignalExists("host-complete")) Finish();
                break;
        }
    }

    private void CheckClientJoined()
    {
        object status = ReadStatus();
        if (!ReadBool(status, "Connected")) return;

        string scene = SceneName();
        object orchestrator = GetField(bridge, "orchestrator");
        object clientState = GetProperty(orchestrator, "ClientState");
        bool activeJoinState = clientState != null && string.Equals(clientState.ToString(), "Active", StringComparison.Ordinal);
        object p2Root = ReadP2Root();
        bool p2Presenter = HasP2Presenter();
        int ackCount = ReadInt(orchestrator, "clientAckCount");
        bool sameScene = !string.IsNullOrEmpty(expectedScene) && string.Equals(scene, expectedScene, StringComparison.Ordinal);
        if (!IsGameplayScene() || !sameScene || !activeJoinState || p2Root == null || !p2Presenter || ackCount < 1) return;
        object monitor = GetField(bridge, "healthMonitor");
        object healthSample = GetProperty(monitor, "CurrentSample");
        if (healthSample == null || !ReadBool(healthSample, "Active")
            || Convert.ToUInt64(ReadMember(healthSample, "SnapshotsReceived")) < 1) return;

        float elapsed = Time.realtimeSinceStartup - actionStarted;
        Assert(true, "client active in host scene=" + scene);
        Assert(p2Root != null, "client has P2 replica root");
        Assert(p2Presenter, "client has active P2 ActorPresenter");
        Assert(activeJoinState, "client JoinState Active");
        Assert(ackCount > 0, "client received InputAck count=" + ackCount);
        VerifyHealthReport("Client");
        RuntimePlaybackProbe.Run(orchestrator, Assert, Log);
        SetField(monitor, "visible", true);
        CaptureScreenshot("client-health.png");
        Assert(elapsed < 15f, "join completed within expected window seconds=" + elapsed.ToString("F2"));
        CaptureScreenshot("client-joined.png");
        WriteSignal("client-ready", scene + "|ack=" + ackCount);
        clientReadyWritten = true;
        Log("CLIENT_READY scene=" + scene + " ack=" + ackCount);
        clientPhase = ClientPhase.WaitHostAcknowledgement;
    }

    private void VerifyHealthReport(string expectedRole)
    {
        object monitor = GetField(bridge, "healthMonitor");
        Assert(monitor != null, "in-game runtime health monitor exists");
        if (monitor == null) return;
        object sample = GetProperty(monitor, "CurrentSample");
        Assert(sample != null, "runtime health sampled actual game state");
        if (sample == null) return;
        Assert(ReadString(sample, "Role") == expectedRole && ReadBool(sample, "Active"), "health role and active state match this endpoint");
        Assert(ReadBool(sample, "CriticalPatchesOk") && ReadBool(sample, "SaveGuardArmed"), "health observes actual patches and native save guard");
        Assert(ReadBool(sample, "P1Bound") && ReadBool(sample, "P2Bound"), "health observes both player bindings or presenters");
        if (expectedRole == "Client")
        {
            ulong sent = Convert.ToUInt64(ReadMember(sample, "InputSent"));
            ulong acked = Convert.ToUInt64(ReadMember(sample, "InputAcked"));
            Assert(sent > 0 && acked > 0 && acked <= sent, "health records actual sent and accepted-ACK sequences");
            double age = Convert.ToDouble(ReadMember(sample, "SnapshotAgeSeconds"));
            Assert(age >= 0 && age < 3, "health records fresh baseline-admitted snapshots age=" + age);
        }
        else
            Assert(Convert.ToUInt64(ReadMember(sample, "InputAccepted")) > 0, "health records host accepted remote input sequence");
        MethodInfo export = monitor.GetType().GetMethod("Export");
        string path = export == null ? string.Empty : export.Invoke(monitor, null) as string;
        Assert(!string.IsNullOrEmpty(path) && File.Exists(path), "runtime health JSON report exported in owned game directory");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            string json = File.ReadAllText(path);
            Assert(json.Contains("\"role\":\"" + expectedRole + "\"") && json.Contains("\"schemaVersion\":1"), "runtime export carries role and schema");
            Assert(!json.Contains("ReconnectTicket") && !json.Contains("reconnectTicket"), "runtime export contains no reconnect ticket fields");
            Log("HEALTH_REPORT=" + path);
        }
    }

    private void RunInvalidPortCheck()
    {
        bool uiRejects = !InvokeUiValidator("TryParsePort", "65536", typeof(int), typeof(string));
        Assert(uiRejects, "lobby UI rejects out-of-range port before action");
        InvokeCreate(0);
        object status = ReadStatus();
        Assert(!ReadBool(status, "IsListening"), "CreateRoom(0) does not listen");
        Assert(!string.IsNullOrEmpty(ReadString(status, "Error")), "invalid port error shown in status");
        InvokeClose();
        Assert(IsOffline(), "invalid-port probe restored offline role");
        Log("NEGATIVE_PORT_COMPLETE");
    }

    private void RunOccupiedPortCheck()
    {
        bool listenerStarted = false;
        try
        {
            occupiedPort = new TcpListener(IPAddress.Any, port);
            occupiedPort.Start();
            listenerStarted = true;
            InvokeCreate(port);
            object status = ReadStatus();
            Assert(!ReadBool(status, "IsListening"), "CreateRoom on occupied TCP port does not report listening");
            Assert(!string.IsNullOrEmpty(ReadString(status, "Error")), "occupied-port failure is visible in lobby UI status");
        }
        catch (SocketException ex)
        {
            Assert(false, "could not reserve requested port for collision case: " + ex.Message);
        }
        finally
        {
            if (listenerStarted && occupiedPort != null)
            {
                occupiedPort.Stop();
                occupiedPort = null;
            }
        }
        InvokeClose();
        Assert(IsOffline(), "occupied-port failure can return to offline title");
        Log("NEGATIVE_OCCUPIED_PORT_COMPLETE");
    }

    private void RunInvalidAddressCheck()
    {
        bool uiRejects = !InvokeUiValidator("TryNormalizeEndpoint", "not a host", typeof(string), typeof(string));
        Assert(uiRejects, "lobby UI rejects malformed address before action");
        InvokeJoin("bad address with spaces");
        object status = ReadStatus();
        Assert(!ReadBool(status, "IsClient") && IsOffline(), "invalid address rejected without installing client role");
        Assert(!string.IsNullOrEmpty(ReadString(status, "Error")), "invalid address error shown in lobby UI status");
        InvokeClose();
        Assert(IsOffline(), "invalid-address probe remains offline");
        Log("NEGATIVE_ADDRESS_COMPLETE");
    }

    private void RunUnavailableAddressCheck()
    {
        string target = "127.0.0.1:" + refusedPort;
        Stopwatch stopwatch = Stopwatch.StartNew();
        InvokeJoin(target);
        stopwatch.Stop();
        Assert(stopwatch.Elapsed.TotalSeconds < 0.5, "unavailable JoinRoom call returned in " + stopwatch.Elapsed.TotalMilliseconds.ToString("F0") + "ms");
        actionStarted = Time.realtimeSinceStartup;
        // PrepareLobbyRole happens synchronously; the transport's DNS/dial remains on its worker.
        SetLobbyScreen("Join", connect, port.ToString());
        expectedScene = ReadSignal("host-ready");
        Log("UNAVAILABLE_JOIN_STARTED endpoint=" + target);
        // A short-lived state marker lets UpdateClient observe the failure before the real retry.
        unavailableCheckPending = true;
    }

    private bool unavailableCheckPending;
    private void CheckUnavailableJoinFailure()
    {
        if (!unavailableCheckPending) return;
        object status = ReadStatus();
        if (ReadBool(status, "IsClient") || ReadBool(status, "Busy")) return;
        if (string.IsNullOrEmpty(ReadString(status, "Error"))) return;
        Assert(IsOffline(), "unavailable local endpoint returns to offline role");
        Assert(ReadP2Root() == null, "unavailable join leaves no P2 replica");
        Assert(Time.realtimeSinceStartup - actionStarted < 15f, "unavailable endpoint fails promptly");
        unavailableCheckPending = false;
        InvokeClose();
        if (manual)
        {
            WriteSignal("client-ui-ready", "join panel open and endpoint prefilled; click Connect");
            clientPhase = ClientPhase.WaitManualJoin;
        }
        else
        {
            clientPhase = ClientPhase.JoinRoom;
        }
        Log("UNAVAILABLE_JOIN_COMPLETE");
    }

    private void InvokeCreate(int requestedPort)
    {
        InvokeAction("CreateRoom", requestedPort);
    }

    private void InvokeJoin(string address)
    {
        InvokeAction("JoinRoom", address);
    }

    private void InvokeClose()
    {
        InvokeAction("CloseRoom");
    }

    private void InvokeAction(string methodName, params object[] args)
    {
        MethodInfo method = FindActionMethod(methodName, args);
        if (method == null) throw new MissingMethodException("ICoopLobbyActions." + methodName);
        try { method.Invoke(lobbyActions, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private MethodInfo FindActionMethod(string name, object[] args)
    {
        Type[] types = new Type[args.Length];
        for (int i = 0; i < args.Length; i++) types[i] = args[i] is int ? typeof(int) : typeof(string);
        return lobbyActionsType.GetMethod(name, types);
    }

    private object ReadStatus()
    {
        MethodInfo method = lobbyActionsType.GetMethod("GetLobbyStatus", Type.EmptyTypes);
        if (method == null) throw new MissingMethodException("ICoopLobbyActions.GetLobbyStatus");
        return method.Invoke(lobbyActions, null);
    }

    private bool InvokeUiValidator(string methodName, string value, Type secondOutType1, Type secondOutType2)
    {
        object screen = GetField(bridge, "lobbyScreen");
        if (screen == null) return false;
        MethodInfo method = screen.GetType().GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) return false;
        object[] args = new object[] { value, secondOutType1 == typeof(int) ? (object)0 : string.Empty, null };
        object result = method.Invoke(null, args);
        return result != null && (bool)result;
    }

    private void OpenLobbyScreen(string page, string address, string portText)
    {
        object screen = GetField(bridge, "lobbyScreen");
        if (screen == null) return;
        SetLobbyScreen(page, address, portText);
        MethodInfo open = screen.GetType().GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic);
        if (open != null) open.Invoke(screen, null);
    }

    private void ScheduleLobbyOpen(string page, string address, string portText)
    {
        deferredLobbyPage = page;
        deferredLobbyAddress = address;
        deferredLobbyPort = portText;
        lobbyOpenPending = true;
    }

    private void SetLobbyScreen(string page, string address, string portText)
    {
        object screen = GetField(bridge, "lobbyScreen");
        if (screen == null) return;
        Type pageType = screen.GetType().GetNestedType("Page", BindingFlags.NonPublic);
        if (pageType != null) SetField(screen, "page", Enum.Parse(pageType, page));
        SetField(screen, "joinAddress", address ?? string.Empty);
        SetField(screen, "portText", portText ?? "57820");
        SetField(screen, "isOpen", true);
    }

    private bool FindBridge()
    {
        if (lobbyActions != null) return true;
        lobbyActionsType = FindLoadedType("IceyCoop.Bridge.ICoopLobbyActions");
        Type pluginType = FindLoadedType("IceyCoop.Bootstrap.IceyCoopPlugin");
        if (lobbyActionsType == null || pluginType == null) return false;

        Component found;
        try { found = UnityEngine.Object.FindObjectOfType(pluginType) as Component; }
        catch { return false; }
        if (found == null) return false;
        plugin = found;
        bridge = GetField(plugin, "bridge");
        if (bridge == null || !lobbyActionsType.IsInstanceOfType(bridge)) return false;
        lobbyActions = bridge;
        if (installerProbe && !portableBootChecked)
        {
            VerifyPortableBootOptions();
            portableBootChecked = true;
        }
        Log("PLUGIN_BRIDGE_FOUND type=" + bridge.GetType().FullName + " scene=" + SceneName());
        return true;
    }

    private void VerifyPortableBootOptions()
    {
        object options = GetField(bridge, "options");
        string gameRoot = ReadString(options, "GameRoot");
        string projectRoot = ReadString(options, "ProjectRoot");
        string logRoot = ReadString(options, "LogRoot");
        string expectedGameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string expectedProjectRoot = Path.GetFullPath(Path.Combine(Path.Combine(expectedGameRoot, "BepInEx"), "icey-coop"));
        string expectedLogRoot = Path.GetFullPath(Path.Combine(expectedProjectRoot, "logs"));
        Assert(PathEquals(gameRoot, expectedGameRoot), "portable BootOptions.GameRoot resolves to this game copy: " + gameRoot);
        Assert(PathEquals(projectRoot, expectedProjectRoot), "portable BootOptions.ProjectRoot is GameRoot/BepInEx/icey-coop: " + projectRoot);
        Assert(PathEquals(logRoot, expectedLogRoot), "portable BootOptions.LogRoot is ProjectRoot/logs: " + logRoot);
        Log("PORTABLE_BOOT_OPTIONS game=" + gameRoot + " project=" + projectRoot + " logs=" + logRoot);
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Exercise the real private startup method with each invalid precondition in isolation.
    // This runs synchronously inside one Update and always shuts down the temporary orchestrator
    // before the bridge's next Update can tick it or create P2.
    private void RunStartupGateNegativeCases()
    {
        RunStartupGateCase("critical patches missing", "criticalPatchesOk", false, true, true);
        RunStartupGateCase("T02 save patch missing", "t02Passed", true, false, true);
        RunStartupGateCase("native save guard disarmed", null, true, true, false);
        Log("STARTUP_GATE_NEGATIVE_CASES_COMPLETE");
    }

    private void RunStartupGateCase(string label, string falseField, bool critical, bool t02, bool armGuard)
    {
        object options = GetField(bridge, "options");
        Type contextType = FindLoadedType("IceyCoop.GameBridge.BridgeContext");
        Type guardType = FindLoadedType("IceyCoop.GameBridge.Progression.NativeSaveGuard");
        object previousOrchestrator = GetField(bridge, "orchestrator");
        object previousCritical = GetField(bridge, "criticalPatchesOk");
        object previousT02 = GetField(bridge, "t02Passed");
        object previousLobbyError = GetField(bridge, "lobbyError");
        string previousRole = ReadString(options, "Role");
        bool previousClient = ReadBool(contextType, "IsClient");
        bool previousHost = ReadBool(contextType, "IsHost");
        bool previousOffline = ReadBool(contextType, "IsOffline");
        bool previousArmed = ReadBool(guardType, "IsArmed");
        int previousTcpPort = ReadInt(options, "TcpPort");
        int previousUdpPort = ReadInt(options, "UdpPort");
        object temporaryOrchestrator = null;
        int testPort = GetEphemeralTcpPort();
        int testUdpPort = testPort == 65535 ? testPort - 1 : testPort + 1;
        try
        {
            SetProperty(options, "Role", "host");
            SetProperty(options, "TcpPort", testPort);
            SetProperty(options, "UdpPort", testUdpPort);
            SetField(bridge, "criticalPatchesOk", falseField == "criticalPatchesOk" ? (object)false : critical);
            SetField(bridge, "t02Passed", falseField == "t02Passed" ? (object)false : t02);
            InvokeStatic(guardType, armGuard ? "Arm" : "Disarm");

            MethodInfo start = bridge.GetType().GetMethod("StartSessionIfRequested", BindingFlags.Instance | BindingFlags.NonPublic);
            if (start == null) throw new MissingMethodException("ProbeBridge.StartSessionIfRequested");
            try { start.Invoke(bridge, null); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }

            temporaryOrchestrator = GetField(bridge, "orchestrator");
            Assert(temporaryOrchestrator != null, label + ": startup method created an inspectable orchestrator");
            Assert(!ReadBool(temporaryOrchestrator, "IsListening"), label + ": orchestrator is not listening");
            Assert(GetField(temporaryOrchestrator, "link") == null, label + ": no network link was created");
            Assert(ReadBool(contextType, "IsOffline"), label + ": BridgeContext stayed offline");
            Assert(CanBindTcp(testPort), label + ": independent TCP port remains unbound");
            Assert(CanBindUdp(testUdpPort), label + ": independent UDP port remains unbound");
            Log("STARTUP_GATE_CASE label=" + label + " tcp=" + testPort + " udp=" + testUdpPort);
        }
        finally
        {
            object currentOrchestrator = GetField(bridge, "orchestrator");
            if (temporaryOrchestrator == null && !ReferenceEquals(currentOrchestrator, previousOrchestrator))
                temporaryOrchestrator = currentOrchestrator;
            try
            {
                if (temporaryOrchestrator != null && !ReferenceEquals(temporaryOrchestrator, previousOrchestrator))
                    InvokeInstance(temporaryOrchestrator, "Shutdown");
            }
            finally
            {
                SetField(bridge, "orchestrator", previousOrchestrator);
                SetField(bridge, "criticalPatchesOk", previousCritical);
                SetField(bridge, "t02Passed", previousT02);
                SetField(bridge, "lobbyError", previousLobbyError);
                SetProperty(options, "Role", previousRole);
                SetProperty(options, "TcpPort", previousTcpPort);
                SetProperty(options, "UdpPort", previousUdpPort);
                InvokeStatic(guardType, previousArmed ? "Arm" : "Disarm");
                InvokeStatic(contextType, "SetRole", previousClient, previousHost, previousOffline);
            }
        }
    }

    private static int GetEphemeralTcpPort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static bool CanBindTcp(int port)
    {
        TcpListener listener = null;
        try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); return true; }
        catch { return false; }
        finally { if (listener != null) listener.Stop(); }
    }

    private static bool CanBindUdp(int port)
    {
        UdpClient listener = null;
        try { listener = new UdpClient(port); return true; }
        catch { return false; }
        finally { if (listener != null) listener.Close(); }
    }

    private static void InvokeStatic(Type type, string methodName, params object[] args)
    {
        if (type == null) throw new TypeLoadException("Required type missing for " + methodName);
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(type.FullName + "." + methodName);
        try { method.Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static void InvokeInstance(object target, string methodName)
    {
        if (target == null) return;
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(target.GetType().FullName + "." + methodName);
        try { method.Invoke(target, null); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static void SetProperty(object target, string name, object value)
    {
        if (target == null) throw new ArgumentNullException("target");
        PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) throw new MissingMemberException(target.GetType().FullName + "." + name);
        property.SetValue(target, value, null);
    }

    private static Type FindLoadedType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }

    private static object GetField(object target, string name)
    {
        if (target == null) return null;
        Type type = target as Type;
        FieldInfo field = (type ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) return null;
        return field.GetValue(field.IsStatic ? null : target);
    }

    private static void SetField(object target, string name, object value)
    {
        if (target == null) return;
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) field.SetValue(target, value);
    }

    private static object GetProperty(object target, string name)
    {
        if (target == null) return null;
        Type type = target as Type;
        PropertyInfo property = (type ?? target.GetType()).GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) return null;
        MethodInfo getter = property.GetGetMethod(true);
        return property.GetValue(getter != null && getter.IsStatic ? null : target, null);
    }

    private static bool ReadBool(object target, string name)
    {
        object value = ReadMember(target, name);
        return value != null && (bool)value;
    }

    private static int ReadInt(object target, string name)
    {
        object value = ReadMember(target, name);
        return value == null ? 0 : Convert.ToInt32(value);
    }

    private static string ReadString(object target, string name)
    {
        object value = ReadMember(target, name);
        return value == null ? string.Empty : Convert.ToString(value);
    }

    private static object ReadMember(object target, string name)
    {
        if (target == null) return null;
        Type suppliedType = target as Type;
        Type type = suppliedType ?? target.GetType();
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) return field.GetValue(field.IsStatic || suppliedType != null ? null : target);
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) return null;
        MethodInfo getter = property.GetGetMethod(true);
        return property.GetValue(getter != null && getter.IsStatic || suppliedType != null ? null : target, null);
    }

    private void LogHostStartStallDiagnostics()
    {
        object status = ReadStatus();
        Log("HOST_START_STALL_DIAG scene=" + SceneName()
            + " statusError=" + SafeLogValue(ReadMember(status, "Error"))
            + " state=" + SafeLogValue(ReadMember(status, "State"))
            + " listening=" + ReadBool(status, "IsListening")
            + " host=" + ReadBool(status, "IsHost"));

        Type saveManager = FindLoadedType("SaveManager");
        Log("HOST_START_STALL_DIAG SaveManager.IsAutoSaveDataExists="
            + SafeLogValue(ReadMember(saveManager, "IsAutoSaveDataExists")));

        Type gameRegistry = FindLoadedType("R");
        object gameData = ReadMember(gameRegistry, "GameData");
        Log("HOST_START_STALL_DIAG R.GameData.SceneName="
            + SafeLogValue(ReadMember(gameData, "SceneName")));

        Type uiType = FindLoadedType("UIStartController");
        if (uiType == null)
        {
            Log("HOST_START_STALL_DIAG UIStartController=<type-not-found>");
            return;
        }

        Component ui = null;
        try { ui = UnityEngine.Object.FindObjectOfType(uiType) as Component; }
        catch (Exception ex) { Log("HOST_START_STALL_DIAG UIStartController lookup=" + SafeLogValue(ex.Message)); }
        if (ui == null)
        {
            Log("HOST_START_STALL_DIAG UIStartController=<instance-not-found>");
            return;
        }

        object mainLayer = ReadMember(ui, "_mainLayer");
        Log("HOST_START_STALL_DIAG UIStartController._mainLayer=" + DescribeReference(mainLayer));
        LogHostStartUiEvidence("25-second-stall");
        LogReferenceFieldNulls("UIStartController", ui, uiType);
        if (mainLayer != null)
            LogReferenceFieldNulls("UIStartController._mainLayer", mainLayer, mainLayer.GetType());
    }

    private void LogHostStartUiEvidence(string reason)
    {
        string prefix = "HOST_START_UI_EVIDENCE reason=" + reason + " ";
        Type controllerType = FindLoadedType("UIStartController");
        if (controllerType == null)
        {
            Log(prefix + "controllerType=<type-not-found> controllerCount=<unknown>");
            return;
        }

        Array controllers = null;
        try { controllers = UnityEngine.Object.FindObjectsOfType(controllerType) as Array; }
        catch (Exception ex) { Log(prefix + "controllerCount=<error:" + SafeLogValue(ex.Message) + ">"); }
        int count = controllers == null ? 0 : controllers.Length;
        Log(prefix + "controllerCount=" + count);
        if (count == 0) return;

        Component controller = controllers.GetValue(0) as Component;
        if (controller == null)
        {
            Log(prefix + "controller[0]=" + DescribeReference(controllers.GetValue(0)) + " (not Component)");
            return;
        }

        object mainLayer = ReadMember(controller, "_mainLayer");
        GameObject layerObject = AsGameObject(mainLayer);
        if (layerObject == null)
        {
            Log(prefix + "_mainLayer=" + DescribeReference(mainLayer) + " gameObject=<null>");
            return;
        }

        Type widgetType = FindLoadedType("UIWidget");
        if (widgetType == null)
            widgetType = FindLoadedTypeByShortName("UIWidget");
        bool hasWidget = false;
        string widgetState = widgetType == null ? "<type-not-found>" : "false";
        if (widgetType != null)
        {
            try
            {
                Component widget = layerObject.GetComponent(widgetType);
                hasWidget = widget != null;
                widgetState = hasWidget ? "true" : "false";
            }
            catch (Exception ex) { widgetState = "<error:" + SafeLogValue(ex.Message) + ">"; }
        }

        Log(prefix + "_mainLayer=" + DescribeReference(mainLayer)
            + " path=" + TransformPath(layerObject.transform)
            + " activeSelf=" + layerObject.activeSelf
            + " activeInHierarchy=" + layerObject.activeInHierarchy
            + " UIWidget=" + widgetState);
    }

    private static GameObject AsGameObject(object value)
    {
        GameObject gameObject = value as GameObject;
        if (gameObject != null) return gameObject;
        Component component = value as Component;
        if (component != null) return component.gameObject;
        return ReadMember(value, "gameObject") as GameObject;
    }

    private static string TransformPath(Transform transform)
    {
        if (transform == null) return "<null-transform>";
        string path = transform.name;
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }

    private static Type FindLoadedTypeByShortName(string shortName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            try
            {
                Type[] types = assemblies[i].GetTypes();
                for (int j = 0; j < types.Length; j++)
                    if (string.Equals(types[j].Name, shortName, StringComparison.Ordinal)) return types[j];
            }
            catch { }
        }
        return null;
    }

    private void LogReferenceFieldNulls(string label, object target, Type type)
    {
        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            if (field.IsStatic || field.FieldType == typeof(string) || field.FieldType.IsPrimitive
                || field.FieldType.IsEnum || field.FieldType.IsValueType) continue;
            object value = null;
            string state;
            try
            {
                value = field.GetValue(target);
                state = DescribeReference(value);
            }
            catch (Exception ex) { state = "<read-error:" + SafeLogValue(ex.Message) + ">"; }
            Log("HOST_START_STALL_DIAG " + label + "." + field.Name + "=" + state);
        }
    }

    private static string DescribeReference(object value)
    {
        if (value == null) return "<null>";
        Array array = value as Array;
        if (array != null) return value.GetType().Name + "[" + array.Length + "]";
        return value.GetType().FullName;
    }

    private static string SafeLogValue(object value)
    {
        if (value == null) return "<null>";
        return Convert.ToString(value).Replace('\r', ' ').Replace('\n', ' ');
    }

    private static object ReadP2Root()
    {
        Type context = FindLoadedType("IceyCoop.GameBridge.BridgeContext");
        return context == null ? null : GetField(context, "P2Root");
    }

    private static bool HasP2Presenter()
    {
        Type factory = FindLoadedType("IceyCoop.GameBridge.World.ReplicaFactory");
        object value = GetField(factory, "players");
        IDictionary players = value as IDictionary;
        if (players == null) return false;
        foreach (DictionaryEntry entry in players)
        {
            object presenter = entry.Value;
            if (presenter == null) continue;
            if (ReadInt(presenter, "PlayerId") == 2 && GetField(presenter, "Transform") != null) return true;
        }
        return false;
    }

    private bool HasP2Root()
    {
        GameObject root = ReadP2Root() as GameObject;
        return root != null;
    }

    private bool IsOffline()
    {
        object status = ReadStatus();
        return !ReadBool(status, "IsHost") && !ReadBool(status, "IsClient");
    }

    private bool IsTitle()
    {
        return string.Equals(SceneName(), "ui_start", StringComparison.Ordinal);
    }

    private bool IsGameplayScene()
    {
        string scene = SceneName();
        return !string.IsNullOrEmpty(scene) && scene.Length >= 6 && scene[0] == 'C'
            && scene.IndexOf('L') > 0 && scene.IndexOf('S') > scene.IndexOf('L');
    }

    private static string SceneName()
    {
        try
        {
            Type level = FindLoadedType("LevelManager");
            object scene = GetProperty(level, "SceneName");
            if (scene != null) return Convert.ToString(scene);
        }
        catch { }
#pragma warning disable 618
        return Application.loadedLevelName;
#pragma warning restore 618
    }

    private void CaptureScreenshot(string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(output), name);
        if (File.Exists(path))
        {
            Assert(false, "screenshot path already exists; refusing overwrite: " + path);
            return;
        }
        try
        {
            Application.CaptureScreenshot(path);
            Log("SCREENSHOT_REQUESTED " + path);
        }
        catch (Exception ex) { Assert(false, "screenshot capture failed: " + ex.Message); }
    }

    private void WriteSignal(string name, string value)
    {
        string path = Path.Combine(signals, name + ".txt");
        using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.Write(value ?? string.Empty);
        }
        Log("SIGNAL " + name + "=" + value);
    }

    private bool SignalExists(string name)
    {
        return File.Exists(Path.Combine(signals, name + ".txt"));
    }

    private string ReadSignal(string name)
    {
        string path = Path.Combine(signals, name + ".txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }

    private void Assert(bool condition, string description)
    {
        assertions++;
        if (!condition) failures++;
        Log((condition ? "PASS " : "FAIL ") + description);
    }

    private void Log(string text)
    {
        string line = DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine;
        detail.Append(line);
        if (!string.IsNullOrEmpty(output)) File.AppendAllText(output, line);
    }

    private void Finish()
    {
        if (finished) return;
        finished = true;
        Log("RESULT role=" + role + " assertions=" + assertions + " failures=" + failures
            + " mainScreenshot=" + menuCaptured + " panelScreenshot=" + panelCaptured
            + " hostReady=" + hostReadyWritten + " clientReady=" + clientReadyWritten
            + " clientDone=" + clientDoneWritten + " hostComplete=" + hostCompleteWritten);
        enabled = false;
    }

    private void FailAndFinish(string reason)
    {
        Assert(false, reason);
        Finish();
    }

    private string CurrentPhase()
    {
        return role == "host" ? hostPhase.ToString() : clientPhase.ToString();
    }

    private void OnDestroy()
    {
        if (occupiedPort != null)
        {
            try { occupiedPort.Stop(); } catch { }
            occupiedPort = null;
        }
        if (!finished && !string.IsNullOrEmpty(output))
        {
            try { FailAndFinish("probe destroyed before completing role=" + role + " phase=" + CurrentPhase()); }
            catch { }
        }
    }
}
