using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using IceyCoop.Bridge;
using IceyCoop.Core;
using IceyCoop.GameBridge.Diagnostics;
using IceyCoop.GameBridge.Lobby;
using IceyCoop.GameBridge.Network;
using IceyCoop.GameBridge.Presentation;
using IceyCoop.GameBridge.Progression;
using IceyCoop.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IceyCoop.GameBridge
{
    internal sealed partial class ProbeBridge
    {
        private CoopLobbyScreen lobbyScreen;
        private TcpUdpLink lobbyLink;
        private string lobbyError = string.Empty;
        private string lobbyPeer = string.Empty;
        private string[] lobbyAddresses = new string[0];
        private int lobbyPort = LobbyAddress.DefaultPort;
        private bool lobbyOwnedSession;
        private bool returningTitle;
        private bool returnWasClient;
        private float returnDeadline, nextTitleLoad, joinDeadline;
        private bool nativeLobbyStartPending;
        private bool nativeLobbyStartCommitted;
        private bool closeAfterNativeStart;
        private bool nativeLobbyStartObservedGameScene;
        private bool nativeLobbyStartTimeoutReported;
        private float nativeLobbyStartCommittedAt;
        private float nativeLobbyStartReleaseAt;
        private UIStartController pendingNativeStartController;
        private Component pendingNativeStartLayer;
        private FieldInfo pendingNativeStartWidgetField;
        private int pendingNativeStartFrame;
        private Scene pendingNativeStartScene;
        private bool pendingNativeStartActivatedLayer;

        private static bool IsTitle()
        {
            return string.Equals(Application.loadedLevelName, "ui_start", StringComparison.Ordinal)
                && string.Equals(LevelManager.SceneName, "ui_start", StringComparison.Ordinal);
        }

        private void InitializeLobby()
        {
            lobbyPort = options.TcpPort;
            lobbyScreen = new CoopLobbyScreen(this, Path.Combine(options.GameRoot, "BepInEx/icey-coop/lobby-address.txt"));
            try
            {
                List<string> addresses = new List<string>();
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ip in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(ip.Address)) continue;
                        string value = ip.Address.ToString();
                        if (!addresses.Contains(value)) addresses.Add(value);
                    }
                }
                if (addresses.Count == 0) addresses.Add("127.0.0.1");
                lobbyAddresses = addresses.ToArray();
            }
            catch { lobbyAddresses = new string[] { "127.0.0.1" }; }
        }

        public void OnGUI()
        {
            if (lobbyScreen != null) lobbyScreen.Draw();
            if (healthMonitor != null) healthMonitor.Draw();
        }

        private void TickLobby()
        {
            TickNativeLobbyStartCommit();
            if (nativeLobbyStartPending) TickPendingNativeLobbyStart();
            if (closeAfterNativeStart && !nativeLobbyStartCommitted)
            {
                closeAfterNativeStart = false;
                CloseRoom();
            }

            if (returningTitle)
            {
                if (IsTitle())
                {
                    returningTitle = false;
                    try { PrepareLobbyRole("offline"); }
                    catch (Exception ex) { lobbyError = "返回单机失败：" + ex.Message; }
                }
                else if (Time.realtimeSinceStartup > returnDeadline)
                {
                    returningTitle = false;
                    lobbyError = "返回标题超时，请再次选择退出联机";
                }
                else if (Time.realtimeSinceStartup > nextTitleLoad)
                {
                    nextTitleLoad = Time.realtimeSinceStartup + 3f;
                    World.SceneLoadGate.LoadAuthorized("ui_start");
                }
            }
            if (lobbyOwnedSession && !returningTitle && BridgeContext.IsClient && orchestrator != null
                && orchestrator.ClientState == JoinState.Handshaking)
            {
                string transportError = lobbyLink == null ? string.Empty : lobbyLink.LastConnectError;
                if (!string.IsNullOrEmpty(transportError) || Time.realtimeSinceStartup > joinDeadline)
                {
                    string error = !string.IsNullOrEmpty(transportError) ? "连接失败：" + transportError : "连接超时，请检查主机地址、端口和组网连接";
                    if (IsTitle())
                    {
                        try { PrepareLobbyRole("offline"); } catch (Exception ex) { BridgeContext.Error("[lobby] failed connect cleanup: " + ex); }
                    }
                    lobbyError = error;
                }
            }
            if (lobbyScreen != null) lobbyScreen.Tick();
        }

        public CoopLobbyStatus GetLobbyStatus()
        {
            CoopLobbyStatus s = new CoopLobbyStatus();
            s.IsMainMenu = IsTitle(); s.Port = lobbyPort; s.HostAddresses = lobbyAddresses; s.Peer = lobbyPeer;
            s.IsHost = !BridgeContext.IsOffline && BridgeContext.IsHost;
            s.IsClient = !BridgeContext.IsOffline && BridgeContext.IsClient;
            s.Connected = orchestrator != null && orchestrator.IsActive && !BridgeContext.PeerDetached;
            s.IsListening = orchestrator != null && orchestrator.IsListening;
            s.Error = lobbyError;
            if (string.IsNullOrEmpty(s.Error) && orchestrator != null) s.Error = FriendlyLobbyError(orchestrator.LastError);
            s.CanCancel = !closeAfterNativeStart && (returningTitle || s.IsHost || s.IsClient);
            s.Busy = returningTitle || closeAfterNativeStart || (s.IsClient && !s.Connected && string.IsNullOrEmpty(s.Error));
            s.CanStart = s.IsMainMenu && !returningTitle && !closeAfterNativeStart && !nativeLobbyStartPending
                && !nativeLobbyStartCommitted && (BridgeContext.IsOffline || !string.IsNullOrEmpty(s.Error));
            s.State = "未联机";
            if (returningTitle) s.State = "正在退出联机并返回标题";
            else if (s.IsHost)
                s.State = s.Connected ? "好友已加入 · 2 / 2" : (s.IsListening ? (s.IsMainMenu ? "房间已创建，进入游戏后好友可加入" : "房间已创建，等待好友 · 1 / 2") : "正在创建房间");
            else if (s.IsClient)
                s.State = s.Connected ? "已加入房间 · 你是 P2" : (lobbyLink != null && lobbyLink.IsConnecting ? "正在连接主机" :
                    (orchestrator != null && orchestrator.ClientState == JoinState.Loading ? (s.IsMainMenu ? "等待主机进入关卡" : "正在加载并同步房间") : "正在验证房间与游戏版本"));
            if (!string.IsNullOrEmpty(s.Error)) { s.Busy = false; s.State = "连接未完成"; }
            if (closeAfterNativeStart) { s.Busy = true; s.State = "等待载入完成后返回标题"; }
            return s;
        }

        private static string FriendlyLobbyError(string error)
        {
            if (string.IsNullOrEmpty(error)) return string.Empty;
            if (error.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0)
                return "双方联机版本不一致，请使用相同版本（" + error + "）";
            if (error.IndexOf("bind", StringComparison.OrdinalIgnoreCase) >= 0)
                return "端口被占用或无法监听，请换一个端口（" + error + "）";
            return error;
        }

        private void CancelPendingNativeLobbyStart(bool restoreLayer)
        {
            if (restoreLayer && pendingNativeStartActivatedLayer && pendingNativeStartLayer != null
                && pendingNativeStartLayer.gameObject.scene.IsValid())
                pendingNativeStartLayer.gameObject.SetActive(false);
            nativeLobbyStartPending = false;
            pendingNativeStartController = null;
            pendingNativeStartLayer = null;
            pendingNativeStartWidgetField = null;
            pendingNativeStartFrame = 0;
            pendingNativeStartScene = new Scene();
            pendingNativeStartActivatedLayer = false;
        }

        private void TickNativeLobbyStartCommit()
        {
            if (!nativeLobbyStartCommitted) return;
            Scene scene = SceneManager.GetActiveScene();
            string levelName = LevelManager.SceneName;
            bool gameSceneReady = scene.IsValid()
                && !string.Equals(scene.name, "ui_start", StringComparison.Ordinal)
                && string.Equals(scene.name, levelName, StringComparison.Ordinal);
            if (gameSceneReady)
            {
                if (!nativeLobbyStartObservedGameScene)
                {
                    nativeLobbyStartObservedGameScene = true;
                    nativeLobbyStartReleaseAt = Time.time + 3.1f;
                }
                else if (Time.time >= nativeLobbyStartReleaseAt)
                {
                    nativeLobbyStartCommitted = false;
                    nativeLobbyStartObservedGameScene = false;
                    if (nativeLobbyStartTimeoutReported && lobbyError == "原生进入流程仍在运行，请等待载入完成后再退出联机")
                        lobbyError = string.Empty;
                    nativeLobbyStartTimeoutReported = false;
                }
            }
            else if (IsTitle() && !nativeLobbyStartTimeoutReported
                && Time.realtimeSinceStartup - nativeLobbyStartCommittedAt > 45f)
            {
                nativeLobbyStartTimeoutReported = true;
                lobbyError = "原生进入流程仍在运行，请等待载入完成后再退出联机";
                BridgeContext.Error("[lobby] native start is still committed on ui_start after 45 seconds");
            }
        }

        private static UIStartController GetUniqueTitleController(out Scene titleScene)
        {
            titleScene = SceneManager.GetActiveScene();
            if (!titleScene.IsValid() || !string.Equals(titleScene.name, "ui_start", StringComparison.Ordinal))
                throw new InvalidOperationException("当前活动场景不是 ui_start");

            UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(typeof(UIStartController));
            List<UIStartController> matches = new List<UIStartController>();
            for (int i = 0; i < all.Length; i++)
            {
                UIStartController candidate = all[i] as UIStartController;
                if (candidate == null) continue;
                Scene scene = candidate.gameObject.scene;
                if (!scene.IsValid() || scene != titleScene) continue;
                matches.Add(candidate);
            }
            if (matches.Count != 1)
                throw new InvalidOperationException("标题场景 UIStartController 数量不是唯一值：" + matches.Count);

            UIStartController menu = matches[0];
            if (!menu.isActiveAndEnabled)
                throw new InvalidOperationException("标题 UIStartController 当前未激活");
            return menu;
        }

        private static string GetTransformPath(Transform current)
        {
            string path = current == null ? "<null>" : current.name;
            while (current != null && current.parent != null)
            {
                current = current.parent;
                path = current.name + "/" + path;
            }
            return path;
        }

        private void ScheduleNativeLobbyStart()
        {
            if (nativeLobbyStartPending || nativeLobbyStartCommitted)
                throw new InvalidOperationException("原生开始流程已在等待或已启动");

            Scene titleScene;
            UIStartController menu = GetUniqueTitleController(out titleScene);
            FieldInfo mainLayerField = typeof(UIStartController).GetField("_mainLayer", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mainLayerField == null) throw new MissingFieldException(typeof(UIStartController).FullName, "_mainLayer");
            Component layer = mainLayerField.GetValue(menu) as Component;
            if (layer == null) throw new InvalidOperationException("标题 UIStartController._mainLayer 为空");
            if (!layer.gameObject.scene.IsValid() || layer.gameObject.scene != titleScene)
                throw new InvalidOperationException("标题主菜单层不属于当前 ui_start 场景");

            Transform parent = layer.transform.parent;
            if (parent == null || !parent.gameObject.activeInHierarchy)
                throw new InvalidOperationException("标题主菜单层的父物体未激活：" + GetTransformPath(layer.transform));
            Component widget = layer.GetComponent("UIWidget");
            if (widget == null)
                throw new InvalidOperationException("标题主菜单层缺少 UIWidget：" + GetTransformPath(layer.transform));
            FieldInfo widgetField = layer.GetType().GetField("_widget", BindingFlags.Instance | BindingFlags.NonPublic);
            if (widgetField == null) throw new MissingFieldException(layer.GetType().FullName, "_widget");

            pendingNativeStartController = menu;
            pendingNativeStartLayer = layer;
            pendingNativeStartWidgetField = widgetField;
            pendingNativeStartFrame = Time.frameCount;
            pendingNativeStartScene = titleScene;
            pendingNativeStartActivatedLayer = !layer.gameObject.activeSelf;
            bool activatedLayer = pendingNativeStartActivatedLayer;
            if (!layer.gameObject.activeInHierarchy)
            {
                if (layer.gameObject.activeSelf)
                    throw new InvalidOperationException("标题主菜单层自身已启用但父层级未激活：" + GetTransformPath(layer.transform));
                layer.gameObject.SetActive(true);
                if (!layer.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("标题主菜单层激活后仍不在活动层级中：" + GetTransformPath(layer.transform));
            }

            pendingNativeStartActivatedLayer = activatedLayer;
            nativeLobbyStartPending = true;
        }

        private void TickPendingNativeLobbyStart()
        {
            if (Time.frameCount <= pendingNativeStartFrame) return;
            try
            {
                Scene scene = SceneManager.GetActiveScene();
                if (!IsTitle() || !scene.IsValid() || scene != pendingNativeStartScene)
                    throw new InvalidOperationException("等待原生开始时标题场景已改变");
                if (pendingNativeStartController == null || !pendingNativeStartController.isActiveAndEnabled)
                    throw new InvalidOperationException("等待原生开始时标题控制器已失效");
                if (pendingNativeStartLayer == null || !pendingNativeStartLayer.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("等待原生开始时主菜单层未激活");
                Transform parent = pendingNativeStartLayer.transform.parent;
                if (parent == null || !parent.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("等待原生开始时主菜单层的父物体未激活");
                Component actualWidget = pendingNativeStartLayer.GetComponent("UIWidget");
                Component cachedWidget = pendingNativeStartWidgetField.GetValue(pendingNativeStartLayer) as Component;
                if (actualWidget == null || cachedWidget == null || !object.ReferenceEquals(actualWidget, cachedWidget))
                    throw new InvalidOperationException("主菜单层 Awake 后仍未缓存其真实 UIWidget");

                MethodInfo start = typeof(UIStartController).GetMethod("OnStartClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (start == null) throw new MissingMethodException(typeof(UIStartController).FullName, "OnStartClick");
                UIStartController menu = pendingNativeStartController;
                CancelPendingNativeLobbyStart(false);
                nativeLobbyStartCommitted = true;
                nativeLobbyStartObservedGameScene = false;
                nativeLobbyStartTimeoutReported = false;
                nativeLobbyStartCommittedAt = Time.realtimeSinceStartup;
                start.Invoke(menu, null);
            }
            catch (Exception ex)
            {
                CancelPendingNativeLobbyStart(true);
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                nativeLobbyStartCommitted = false;
                nativeLobbyStartObservedGameScene = false;
                nativeLobbyStartTimeoutReported = false;
                lobbyError = "进入游戏失败：" + cause.Message;
                BridgeContext.Error("[lobby] native start failed: " + cause);
            }
        }

        private void PrepareLobbyRole(string role)
        {
            CancelPendingNativeLobbyStart(true);
            if (nativeLobbyStartCommitted)
                throw new InvalidOperationException("原生开始流程已启动，请等待载入完成后再切换联机状态");
            if (!IsTitle()) throw new InvalidOperationException("请先返回主菜单，再创建或加入房间");
            closeAfterNativeStart = false;
            if (orchestrator != null) orchestrator.Shutdown();
            if (installer != null) installer.UninstallAll();
            BridgeContext.SetRole(false, true, true);
            LobbyRuntimeCleanup.AfterTitleEntered();
            options.Role = role; options.Relay = string.Empty; options.TcpOnly = true; options.DemoInput = false; options.ResumeSave = false;
            p2Created = false; p2Failed = false; p2Attempts = 0; criticalBlockLogged = false;
            lobbyLink = null; lobbyOwnedSession = false;
            BridgeContext.SetRole(role == "client", role != "client", role == "offline");
            installer = new PatchInstaller(trace, new BindingManifest());
            string error;
            bool ok = installer.TryInstallAll(out error);
            string probes = ok ? installer.InstallT03Probes() : error;
            criticalPatchesOk = ok && probes == null;
            if (!criticalPatchesOk)
            {
                installer.UninstallAll(); BridgeContext.SetRole(false, true, true); NativeSaveGuard.Disarm();
                throw new InvalidOperationException("联机补丁检查未通过：" + probes);
            }
            if (role == "offline") NativeSaveGuard.Disarm(); else NativeSaveGuard.Arm();
            orchestrator = new SessionOrchestrator();
            BridgeContext.Info("[lobby] role ready=" + role + " critical=" + criticalPatchesOk);
        }

        public void CreateRoom(int port)
        {
            if (nativeLobbyStartPending || nativeLobbyStartCommitted)
            {
                lobbyError = "正在进入游戏，请勿重复创建房间";
                return;
            }
            if (!GetLobbyStatus().CanStart) return;
            lobbyError = string.Empty;
            if (port < 1 || port > 65535) { lobbyError = "端口必须是 1 到 65535 的整数"; return; }
            try
            {
                PrepareLobbyRole("host");
                lobbyPort = port; options.TcpPort = port; options.UdpPort = port;
                lobbyLink = new TcpUdpLink(true); lobbyOwnedSession = true;
                orchestrator.StartHost(lobbyLink, options);
                if (!orchestrator.IsListening) { lobbyError = FriendlyLobbyError(orchestrator.LastError); return; }
                BridgeContext.Info("[lobby] room created single TCP port=" + port);
                ScheduleNativeLobbyStart();
            }
            catch (Exception ex)
            {
                CancelPendingNativeLobbyStart(true);
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                lobbyError = "创建房间失败：" + cause.Message;
                BridgeContext.Error("[lobby] create failed: " + cause);
            }
        }

        public void JoinRoom(string address)
        {
            if (!GetLobbyStatus().CanStart) return;
            LobbyAddress endpoint; string error;
            if (!LobbyAddress.TryParse(address, out endpoint, out error)) { lobbyError = error; return; }
            lobbyError = string.Empty;
            try
            {
                PrepareLobbyRole("client");
                lobbyPeer = endpoint.ToString(); lobbyPort = endpoint.Port;
                options.Connect = endpoint.ToString(); options.TcpPort = endpoint.Port; options.UdpPort = endpoint.Port;
                lobbyLink = new TcpUdpLink(true); lobbyOwnedSession = true;
                joinDeadline = Time.realtimeSinceStartup + 15f;
                orchestrator.StartClient(lobbyLink, options);
                BridgeContext.Info("[lobby] joining " + endpoint);
            }
            catch (Exception ex) { lobbyError = "加入房间失败：" + ex.Message; BridgeContext.Error("[lobby] join failed: " + ex); }
        }

        public void CloseRoom()
        {
            if (returningTitle) return;
            if (nativeLobbyStartCommitted)
            {
                // Native start discards its Coroutine handle. Preserve authority
                // until it finishes, then honour this close exactly once.
                closeAfterNativeStart = true;
                lobbyError = string.Empty;
                return;
            }
            CancelPendingNativeLobbyStart(true);
            lobbyError = string.Empty;
            if (IsTitle())
            {
                try { PrepareLobbyRole("offline"); } catch (Exception ex) { lobbyError = ex.Message; }
                return;
            }
            if (BridgeContext.IsOffline) return;
            // Keep the old authority/save guard until the title scene really arrives.
            returnWasClient = BridgeContext.IsClient;
            if (orchestrator != null) orchestrator.Shutdown();
            BridgeContext.SetRole(returnWasClient, !returnWasClient, false);
            NativeSaveGuard.Arm();
            returningTitle = true; returnDeadline = Time.realtimeSinceStartup + 20f; nextTitleLoad = 0f;
        }
    }
}
