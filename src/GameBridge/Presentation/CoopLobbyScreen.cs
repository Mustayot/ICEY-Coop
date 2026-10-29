using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using IceyCoop.Bridge;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Lightweight IMGUI lobby view. It only presents state and forwards user
    /// intent through ICoopLobbyActions; networking and scene policy stay in the bridge.
    /// Call Draw from OnGUI, Tick from Update, and Dispose when the owning bridge closes.
    /// </summary>
    public sealed class CoopLobbyScreen : IDisposable
    {
        private enum Page { Create, Join, Status }

        private const int WindowId = 0x1CE7;
        private const float DesignWidth = 820f;
        private const float DesignHeight = 520f;
        private const int DefaultPort = 57000;

        private readonly ICoopLobbyActions actions;
        private readonly string settingsPath;
        private readonly List<Behaviour> disabledCameras = new List<Behaviour>();
        private readonly List<Texture2D> generatedTextures = new List<Texture2D>();

        private CoopLobbyStatus status;
        private bool isOpen;
        private bool disposed;
        private bool skinReady;
        private bool ownsLobbySkin;
        private bool localActionPending;
        private bool wasMainMenu;
        private bool helpExpanded;
        private float localActionDeadline;
        private Page page = Page.Create;
        private string joinAddress;
        private string joinError;
        private string localMessage;
        private string portText = DefaultPort.ToString();
        private string lastFocusedControl;
        private Vector2 addressScroll;
        private Vector2 errorScroll;
        private GUISkin lobbySkin;
        private Font lobbyFont;
        private Rect windowRect;

        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle bodyStyle;
        private GUIStyle mutedStyle;
        private GUIStyle accentStyle;
        private GUIStyle smallStyle;
        private GUIStyle primaryButtonStyle;
        private GUIStyle tabStyle;
        private GUIStyle textFieldStyle;
        private GUIStyle cardStyle;
        private GUIStyle sectionStyle;
        private GUIStyle eyebrowStyle;
        private GUIStyle hintStyle;
        private GUIStyle navStyle;
        private GUIStyle navActiveStyle;
        private GUIStyle secondaryButtonStyle;
        private GUIStyle dangerButtonStyle;
        private GUIStyle endpointStyle;
        private GUIStyle statusStyle;

        public CoopLobbyScreen(ICoopLobbyActions lobbyActions, string userSettingsPath)
        {
            if (lobbyActions == null) throw new ArgumentNullException("lobbyActions");
            actions = lobbyActions;
            settingsPath = userSettingsPath;
            status = new CoopLobbyStatus();
            joinAddress = LoadRecentAddress();
            if (string.IsNullOrEmpty(joinAddress))
                joinAddress = "127.0.0.1:" + DefaultPort;
            if (!string.IsNullOrEmpty(settingsPath))
            {
                try
                {
                    string directory = Path.GetDirectoryName(settingsPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                }
                catch { }
            }
        }

        public bool IsOpen { get { return isOpen; } }

        public void Tick()
        {
            if (disposed) return;
            try
            {
                CoopLobbyStatus current = actions.GetLobbyStatus();
                if (current != null)
                {
                    if (wasMainMenu && !current.IsMainMenu && isOpen) CloseView();
                    wasMainMenu = current.IsMainMenu;
                    status = current;
                }
            }
            catch (Exception ex)
            {
                localMessage = "状态读取失败：" + ex.Message;
            }

            if (localActionPending)
            {
                bool reachedState = status.Busy || status.Connected || status.IsHost
                    || status.IsClient || status.IsListening || !string.IsNullOrEmpty(status.Error);
                if (reachedState || Time.realtimeSinceStartup >= localActionDeadline)
                    localActionPending = false;
            }

            if (isOpen) DisableNativeUiInput();
            else RestoreNativeUiInput();
        }

        public void Draw()
        {
            if (disposed || status == null) return;
            EnsureSkin();

            Matrix4x4 previousMatrix = GUI.matrix;
            GUISkin previousSkin = GUI.skin;
            Color previousColor = GUI.color;
            Color previousBackground = GUI.backgroundColor;
            Color previousContent = GUI.contentColor;
            try
            {
                // Scale by both axes. Never clip the fixed IMGUI artboard on 4:3 or small windows.
                float scale = Mathf.Min(1.15f, Mathf.Min(Screen.width / 1120f, Screen.height / 690f));
                scale = Mathf.Max(0.22f, scale);
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                GUI.skin = lobbySkin;

                float logicalWidth = Screen.width / scale;
                float logicalHeight = Screen.height / scale;
                if (status.IsMainMenu)
                {
                    DrawTitleEntry(logicalWidth, logicalHeight);
                }
                else if (HasRoomContext())
                {
                    DrawInGameStatus(logicalWidth);
                }

                if (isOpen)
                {
                    Paint(new Rect(0f, 0f, logicalWidth, logicalHeight), new Color(0f, 0f, 0f, 0.56f));
                    windowRect = CenteredWindow(logicalWidth, logicalHeight);
                    windowRect = GUI.Window(WindowId, windowRect, DrawWindow, GUIContent.none);
                    HandleWindowKeys();
                }
            }
            finally
            {
                GUI.matrix = previousMatrix;
                GUI.skin = previousSkin;
                GUI.color = previousColor;
                GUI.backgroundColor = previousBackground;
                GUI.contentColor = previousContent;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            isOpen = false;
            RestoreNativeUiInput();
            if (ownsLobbySkin && lobbySkin != null)
            {
                UnityEngine.Object.Destroy(lobbySkin);
                lobbySkin = null;
            }
            for (int i = 0; i < generatedTextures.Count; i++)
            {
                if (generatedTextures[i] != null)
                    UnityEngine.Object.Destroy(generatedTextures[i]);
            }
            generatedTextures.Clear();
            if (lobbyFont != null)
            {
                UnityEngine.Object.Destroy(lobbyFont);
                lobbyFont = null;
            }
        }

        private void DrawTitleEntry(float logicalWidth, float logicalHeight)
        {
            if (isOpen) return;
            Rect panel = new Rect(logicalWidth - 260f, logicalHeight - 123f, 228f, 91f);
            Paint(panel, new Color(0.055f, 0.077f, 0.096f, 0.92f));
            Paint(new Rect(panel.x, panel.y, 3f, panel.height), new Color(0.63f, 0.90f, 0.95f));
            GUI.Label(new Rect(panel.x + 17f, panel.y + 9f, 190f, 17f), "联机 / CO-OP  ·  F8 体检", eyebrowStyle);
            if (GUI.Button(new Rect(panel.x + 17f, panel.y + 31f, 190f, 43f), "进入联机  ›", primaryButtonStyle))
            {
                page = Page.Create;
                Open();
            }
        }

        private void DrawInGameStatus(float logicalWidth)
        {
            if (isOpen) return;
            Rect chip = new Rect(logicalWidth - 294f, 22f, 264f, 52f);
            Paint(chip, new Color(0.045f, 0.067f, 0.083f, 0.93f));
            Paint(new Rect(chip.x, chip.y, 3f, chip.height), new Color(0.52f, 0.89f, 0.90f));
            string role = status.IsHost ? "主机" : status.IsClient ? "客机" : "联机";
            string stateText = StatusLine();
            GUI.Label(new Rect(chip.x + 16f, chip.y + 7f, 214f, 16f), role + " / CO-OP  ·  F8 体检", eyebrowStyle);
            if (GUI.Button(new Rect(chip.x + 12f, chip.y + 24f, 239f, 23f), Truncate(stateText, 15) + "    查看详情 ›", navStyle))
            {
                page = Page.Status;
                Open();
            }
        }

        private void DrawWindow(int id)
        {
            // A restrained ICEY-inspired terminal: layered graphite, light and thin lines.
            Paint(new Rect(0f, 0f, DesignWidth, DesignHeight), new Color(0.054f, 0.072f, 0.089f, 0.985f));
            Stroke(new Rect(0.5f, 0.5f, DesignWidth - 1f, DesignHeight - 1f), new Color(0.37f, 0.44f, 0.48f, 0.75f));
            Paint(new Rect(0f, 0f, 4f, 104f), new Color(0.66f, 0.88f, 0.93f));
            GUI.Label(new Rect(36f, 22f, 210f, 45f), "ICEY_", titleStyle);
            GUI.Label(new Rect(210f, 41f, 328f, 25f), "COOPERATIVE  /  联机协议", eyebrowStyle);
            GUI.Label(new Rect(37f, 73f, 500f, 22f), "与好友，共同进入这个世界。", mutedStyle);
            DrawStatusBadge(new Rect(584f, 39f, 197f, 37f));
            Paint(new Rect(36f, 107f, 748f, 1f), new Color(0.27f, 0.32f, 0.36f, 0.83f));

            DrawNavigation();
            Paint(new Rect(232f, 131f, 552f, 322f), new Color(0.075f, 0.099f, 0.12f, 0.77f));
            Stroke(new Rect(232f, 131f, 552f, 322f), new Color(0.27f, 0.34f, 0.39f, 0.62f));
            Paint(new Rect(232f, 131f, 54f, 2f), new Color(0.59f, 0.86f, 0.92f));

            if (!status.IsMainMenu) DrawStatusPage();
            else if (page == Page.Create) DrawCreatePage();
            else if (page == Page.Join) DrawJoinPage();
            else DrawStatusPage();

            DrawFooter();
            lastFocusedControl = GUI.GetNameOfFocusedControl();
            GUI.DragWindow(new Rect(6f, 0f, 790f, 105f));
        }

        private void DrawNavigation()
        {
            GUI.Label(new Rect(37f, 142f, 168f, 22f), "NAVIGATION  /  模式", eyebrowStyle);
            bool inMenu = status.IsMainMenu;
            bool wasEnabled = GUI.enabled;
            if (inMenu)
            {
                if (GUI.Button(new Rect(37f, 177f, 177f, 44f), "01   创建房间", page == Page.Create ? navActiveStyle : navStyle)) page = Page.Create;
                if (GUI.Button(new Rect(37f, 228f, 177f, 44f), "02   加入房间", page == Page.Join ? navActiveStyle : navStyle)) page = Page.Join;
            }
            else
            {
                GUI.Label(new Rect(42f, 179f, 176f, 50f), "游戏进行中\n请回标题切换模式", mutedStyle);
            }
            if (GUI.Button(new Rect(37f, 280f, 177f, 44f), "03   会话状态", page == Page.Status || !inMenu ? navActiveStyle : navStyle)) page = Page.Status;
            GUI.enabled = wasEnabled;

            Paint(new Rect(37f, 339f, 176f, 1f), new Color(0.29f, 0.36f, 0.39f, 0.7f));
            if (GUI.Button(new Rect(37f, 349f, 174f, 28f), helpExpanded ? "—  收起连接说明" : "+  连接说明", secondaryButtonStyle))
                helpExpanded = !helpExpanded;
            if (helpExpanded)
            {
                GUI.Label(new Rect(42f, 385f, 174f, 62f),
                    "樱花：映射单个 TCP 端口\nRadmin / VPN：填写虚拟 IP\nUU：需支持虚拟局域网", hintStyle);
            }
            else
            {
                GUI.Label(new Rect(42f, 387f, 168f, 44f),
                    "无需账号或匹配服务\n连接方式由玩家自行配置", hintStyle);
            }
        }

        private void DrawCreatePage()
        {
            GUI.Label(new Rect(260f, 153f, 405f, 34f), "创建房间", sectionStyle);
            GUI.Label(new Rect(261f, 190f, 490f, 25f), "你是主机。好友通过地址与端口加入游戏。", mutedStyle);
            GUI.Label(new Rect(261f, 230f, 188f, 25f), "监听端口  /  PORT", eyebrowStyle);
            if (status.IsListening && status.Port > 0) portText = status.Port.ToString();
            GUI.SetNextControlName("icey_host_port");
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !status.IsListening && !status.Busy && !localActionPending;
            portText = GUI.TextField(new Rect(260f, 259f, 198f, 48f), portText, textFieldStyle);
            GUI.enabled = wasEnabled;
            GUI.Label(new Rect(473f, 274f, 266f, 24f), "默认 57000  ·  单 TCP 端口", hintStyle);

            string portError;
            int port;
            bool validPort = TryParsePort(portText, out port, out portError);
            if (status.IsListening)
            {
                // Listening can succeed while native game entry fails. Keep the
                // actionable error visible instead of presenting a ready-only state.
                if (!string.IsNullOrEmpty(status.Error))
                {
                    DrawScrollableMessage(new Rect(261f, 317f, 494f, 45f), status.Error);
                    GUI.Label(new Rect(261f, 369f, 480f, 22f), "可用地址  /  CLICK TO COPY", eyebrowStyle);
                    DrawHostAddresses(new Rect(260f, 398f, 496f, 44f));
                }
                else
                {
                    GUI.Label(new Rect(261f, 320f, 489f, 26f),
                        status.Connected ? "●  好友已连接  ·  会话进行中" : "●  房间已就绪  ·  将地址发给好友", statusStyle);
                    GUI.Label(new Rect(261f, 348f, 480f, 22f), "可用地址  /  CLICK TO COPY", eyebrowStyle);
                    DrawHostAddresses(new Rect(260f, 375f, 496f, 67f));
                }
            }
            else
            {
                string error = !validPort ? portError : !string.IsNullOrEmpty(status.Error) ? status.Error : localMessage;
                if (!string.IsNullOrEmpty(error))
                    DrawScrollableMessage(new Rect(261f, 314f, 498f, 43f), error);
                else
                    GUI.Label(new Rect(261f, 316f, 496f, 29f), "对方需要填写你的 IP / 域名和相同端口。", hintStyle);
                GUI.enabled = wasEnabled && status.CanStart && !status.Busy && !localActionPending && validPort;
                if (GUI.Button(new Rect(260f, 365f, 300f, 51f),
                    status.Busy ? "正在创建房间…" : "创建并进入游戏   ›", primaryButtonStyle))
                {
                    localMessage = string.Empty;
                    localActionPending = true;
                    localActionDeadline = Time.realtimeSinceStartup + 2f;
                    SafeAction(delegate { actions.CreateRoom(port); });
                }
                GUI.enabled = wasEnabled;
                GUI.Label(new Rect(574f, 381f, 182f, 38f), "房间创建后可直接\n开始主机游戏", hintStyle);
            }
        }

        private void DrawJoinPage()
        {
            GUI.Label(new Rect(260f, 153f, 450f, 34f), "加入房间", sectionStyle);
            GUI.Label(new Rect(261f, 190f, 496f, 25f), "输入好友提供的连接地址。", mutedStyle);
            GUI.Label(new Rect(261f, 229f, 493f, 27f), "主机地址  /  IP OR DOMAIN", eyebrowStyle);
            GUI.SetNextControlName("icey_join_address");
            joinAddress = GUI.TextField(new Rect(260f, 260f, 496f, 50f), joinAddress ?? string.Empty, textFieldStyle);
            GUI.Label(new Rect(261f, 319f, 490f, 28f),
                "示例  192.168.1.5:57000    ·    未填端口则使用 57000", hintStyle);

            string normalized;
            string validationError;
            bool validAddress = TryNormalizeEndpoint(joinAddress, out normalized, out validationError);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && status.CanStart && !status.Busy && !localActionPending && validAddress;
            if (GUI.Button(new Rect(260f, 361f, 296f, 50f),
                status.Busy ? "正在连接…" : "连接好友房间   ›", primaryButtonStyle))
            {
                joinError = string.Empty;
                localMessage = string.Empty;
                SaveRecentAddress(normalized);
                localActionPending = true;
                localActionDeadline = Time.realtimeSinceStartup + 2f;
                SafeAction(delegate { actions.JoinRoom(normalized); });
            }
            GUI.enabled = wasEnabled;
            GUI.Label(new Rect(572f, 376f, 182f, 33f), "支持局域网、VPN\n及 TCP 映射地址", hintStyle);

            string error = !validAddress && !string.IsNullOrEmpty(joinAddress) ? validationError
                : !string.IsNullOrEmpty(joinError) ? joinError
                : !string.IsNullOrEmpty(status.Error) ? status.Error : localMessage;
            if (!string.IsNullOrEmpty(error))
                DrawScrollableMessage(new Rect(261f, 413f, 494f, 35f), error);
            else
                GUI.Label(new Rect(261f, 419f, 494f, 25f), "连接地址仅保存在此设备上。", hintStyle);
        }

        private void DrawStatusPage()
        {
            GUI.Label(new Rect(260f, 153f, 490f, 33f), "会话状态", sectionStyle);
            GUI.Label(new Rect(261f, 195f, 129f, 27f), "当前状态", mutedStyle);
            GUI.Label(new Rect(410f, 195f, 340f, 28f), Truncate(StatusLine(), 30), statusStyle);
            Paint(new Rect(261f, 230f, 492f, 1f), new Color(0.29f, 0.35f, 0.40f, 0.65f));
            GUI.Label(new Rect(261f, 243f, 129f, 27f), "联机身份", mutedStyle);
            string role = status.IsHost ? "主机 / HOST" : status.IsClient ? "客机 / CLIENT" : "尚未加入";
            GUI.Label(new Rect(410f, 243f, 339f, 28f), role, bodyStyle);
            Paint(new Rect(261f, 278f, 492f, 1f), new Color(0.29f, 0.35f, 0.40f, 0.65f));
            GUI.Label(new Rect(261f, 289f, 130f, 27f), "房间成员", mutedStyle);
            GUI.Label(new Rect(410f, 289f, 342f, 28f),
                Truncate(string.IsNullOrEmpty(status.Peer) ? "等待好友加入" : status.Peer, 42), bodyStyle);
            GUI.Label(new Rect(261f, 333f, 480f, 23f), "连接地址  /  ADDRESS", eyebrowStyle);
            string error = !string.IsNullOrEmpty(status.Error) ? status.Error : localMessage;
            if (status.IsListening)
                DrawHostAddresses(new Rect(260f, 360f, 496f, string.IsNullOrEmpty(error) ? 79f : 52f));
            else
                GUI.Label(new Rect(260f, 369f, 488f, 32f), "当前没有可分享的主机地址", mutedStyle);
            if (!string.IsNullOrEmpty(error))
                DrawScrollableMessage(new Rect(261f, 414f, 490f, 37f), error);
        }

        private void DrawScrollableMessage(Rect rect, string message)
        {
            float contentWidth = rect.width - 18f;
            float contentHeight = Mathf.Max(rect.height, accentStyle.CalcHeight(new GUIContent(message), contentWidth) + 4f);
            GUI.BeginGroup(rect);
            errorScroll = GUI.BeginScrollView(new Rect(0f, 0f, rect.width, rect.height), errorScroll,
                new Rect(0f, 0f, contentWidth, contentHeight), false, true);
            GUI.Label(new Rect(0f, 0f, contentWidth, contentHeight), message, accentStyle);
            GUI.EndScrollView();
            GUI.EndGroup();
        }

        private void DrawHostAddresses(Rect start)
        {
            if (!status.IsListening) return;
            string[] addresses = status.HostAddresses;
            if (addresses == null || addresses.Length == 0)
            {
                GUI.Label(start, "等待可分享的网卡地址…", mutedStyle);
                return;
            }
            List<string> validAddresses = new List<string>();
            for (int i = 0; i < addresses.Length; i++)
            {
                if (!string.IsNullOrEmpty(addresses[i])) validAddresses.Add(addresses[i]);
            }
            if (validAddresses.Count == 0)
            {
                GUI.Label(start, "等待可分享的网卡地址…", mutedStyle);
                return;
            }
            Paint(start, new Color(0.035f, 0.053f, 0.069f, 0.92f));
            float rowHeight = 31f;
            float contentHeight = Math.Max(start.height, validAddresses.Count * rowHeight);
            GUI.BeginGroup(start);
            addressScroll = GUI.BeginScrollView(new Rect(0f, 0f, start.width, start.height), addressScroll,
                new Rect(0f, 0f, start.width - 20f, contentHeight), false, true);
            for (int i = 0; i < validAddresses.Count; i++)
            {
                float y = i * rowHeight;
                string host = validAddresses[i];
                string endpoint = AddPort(host, status.Port > 0 ? status.Port : ParsePortOrDefault(portText));
                if (i > 0) Paint(new Rect(10f, y, start.width - 42f, 1f), new Color(0.24f, 0.30f, 0.33f, 0.40f));
                GUI.Label(new Rect(14f, y + 4f, start.width - 150f, 24f), new GUIContent(endpoint, endpoint), endpointStyle);
                if (GUI.Button(new Rect(start.width - 131f, y + 2f, 111f, 26f), "复制  ›", secondaryButtonStyle))
                    CopyText(endpoint);
            }
            GUI.EndScrollView();
            GUI.EndGroup();
        }

        private void DrawFooter()
        {
            Paint(new Rect(36f, 460f, 748f, 1f), new Color(0.26f, 0.34f, 0.39f, 0.80f));
            string footerHint = !string.IsNullOrEmpty(localMessage) ? Truncate(localMessage, 36)
                : HasRoomContext() ? "返回仅收起面板，不会断开连接" : "ESC  返回   /   ENTER  确认";
            GUI.Label(new Rect(37f, 476f, 340f, 24f), footerHint, hintStyle);
            bool wasEnabled = GUI.enabled;
            if (status.Busy && status.CanCancel)
            {
                if (GUI.Button(new Rect(413f, 472f, 210f, 35f), "取消连接", dangerButtonStyle))
                {
                    localActionPending = false;
                    SafeAction(delegate { actions.CloseRoom(); });
                }
            }
            else if (HasRoomContext() && (status.CanCancel || status.IsListening || status.Connected))
            {
                string label = status.IsMainMenu ? "关闭房间" : "退出联机并回标题";
                if (GUI.Button(new Rect(413f, 472f, 210f, 35f), label, dangerButtonStyle))
                {
                    localActionPending = false;
                    SafeAction(delegate { actions.CloseRoom(); });
                }
            }
            GUI.enabled = wasEnabled;
            if (GUI.Button(new Rect(650f, 472f, 132f, 35f), "收起面板  ×", secondaryButtonStyle))
                CloseView();
        }

        private void DrawStatusBadge(Rect rect)
        {
            bool good = status.Connected || status.IsListening;
            Color border = good ? new Color(0.35f, 0.76f, 0.70f, 0.78f) : new Color(0.38f, 0.45f, 0.50f, 0.70f);
            Paint(rect, new Color(0.10f, 0.14f, 0.17f, 0.94f));
            Stroke(rect, border);
            Color old = GUI.contentColor;
            GUI.contentColor = good ? new Color(0.63f, 0.96f, 0.87f) : new Color(0.78f, 0.83f, 0.87f);
            GUI.Label(new Rect(rect.x + 12f, rect.y + 4f, rect.width - 23f, rect.height - 8f),
                "●  " + Truncate(StatusLine(), 13), smallStyle);
            GUI.contentColor = old;
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length > limit ? value.Substring(0, limit - 1) + "…" : value;
        }

        private static void Paint(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void Stroke(Rect rect, Color color)
        {
            Paint(new Rect(rect.x, rect.y, rect.width, 1f), color);
            Paint(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            Paint(new Rect(rect.x, rect.y, 1f, rect.height), color);
            Paint(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        private void HandleWindowKeys()
        {
            Event current = Event.current;
            if (current == null || current.type != EventType.KeyDown) return;
            if (current.keyCode == KeyCode.Escape)
            {
                CloseView();
                current.Use();
                return;
            }
            if ((current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                && lastFocusedControl == "icey_join_address" && page == Page.Join)
            {
                TryJoinFromUi();
                current.Use();
            }
            else if ((current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                && lastFocusedControl == "icey_host_port" && page == Page.Create)
            {
                TryCreateFromUi();
                current.Use();
            }
        }

        private void TryJoinFromUi()
        {
            string normalized;
            string error;
            if (!TryNormalizeEndpoint(joinAddress, out normalized, out error))
            {
                joinError = error;
                return;
            }
            if (!status.CanStart || status.Busy || localActionPending) return;
            SaveRecentAddress(normalized);
            localActionPending = true;
            localActionDeadline = Time.realtimeSinceStartup + 2f;
            SafeAction(delegate { actions.JoinRoom(normalized); });
        }

        private void TryCreateFromUi()
        {
            string error;
            int port;
            if (!TryParsePort(portText, out port, out error))
            {
                localMessage = error;
                return;
            }
            if (!status.CanStart || status.Busy || localActionPending) return;
            localActionPending = true;
            localActionDeadline = Time.realtimeSinceStartup + 2f;
            SafeAction(delegate { actions.CreateRoom(port); });
        }

        private void Open()
        {
            isOpen = true;
            localMessage = string.Empty;
            joinError = string.Empty;
            localActionPending = false;
            DisableNativeUiInput();
        }

        private void CloseView()
        {
            isOpen = false;
            localActionPending = false;
            RestoreNativeUiInput();
        }

        private void SafeAction(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                localActionPending = false;
                localMessage = ex.Message;
            }
        }

        private string StatusLine()
        {
            if (!string.IsNullOrEmpty(status.Error)) return "连接失败";
            if (status.Connected) return string.IsNullOrEmpty(status.State) ? "已加入" : status.State;
            if (status.IsListening) return "等待好友加入";
            if (status.Busy) return string.IsNullOrEmpty(status.State) ? "正在连接" : status.State;
            if (status.IsHost) return string.IsNullOrEmpty(status.State) ? "主机就绪" : status.State;
            if (status.IsClient) return string.IsNullOrEmpty(status.State) ? "客机" : status.State;
            return string.IsNullOrEmpty(status.State) ? "未连接" : status.State;
        }

        private bool HasRoomContext()
        {
            return status.Busy || status.Connected || status.IsHost || status.IsClient || status.IsListening;
        }

        private static Rect CenteredWindow(float logicalWidth, float logicalHeight)
        {
            float width = Mathf.Min(DesignWidth, logicalWidth - 28f);
            float height = Mathf.Min(DesignHeight, logicalHeight - 28f);
            float x = Mathf.Max(8f, (logicalWidth - width) * 0.5f);
            float y = Mathf.Max(8f, (logicalHeight - height) * 0.5f);
            return new Rect(x, y, width, height);
        }

        private static bool TryParsePort(string text, out int port, out string error)
        {
            port = DefaultPort;
            error = null;
            int parsed;
            if (string.IsNullOrEmpty(text) || !int.TryParse(text.Trim(), out parsed))
            {
                error = "请输入 1 到 65535 之间的端口。";
                return false;
            }
            if (parsed < 1 || parsed > 65535)
            {
                error = "端口范围是 1 到 65535。";
                return false;
            }
            port = parsed;
            return true;
        }

        private static int ParsePortOrDefault(string text)
        {
            int port;
            string error;
            return TryParsePort(text, out port, out error) ? port : DefaultPort;
        }

        private static bool TryNormalizeEndpoint(string value, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = null;
            string input = value == null ? string.Empty : value.Trim();
            if (input.Length == 0)
            {
                error = "请输入主机 IP 或域名。";
                return false;
            }

            string host = input;
            int port = DefaultPort;
            int colon = input.LastIndexOf(':');
            if (colon >= 0)
            {
                if (input.IndexOf(':') != colon)
                {
                    error = "请使用 IP 或域名:端口 格式；IPv6 地址请联系主机提供可连接地址。";
                    return false;
                }
                host = input.Substring(0, colon).Trim();
                string rawPort = input.Substring(colon + 1).Trim();
                string portError;
                if (!TryParsePort(rawPort, out port, out portError))
                {
                    error = "端口无效：" + portError;
                    return false;
                }
            }

            if (!IsPlausibleHost(host))
            {
                error = "地址格式不正确，请输入 IP 或域名，可选 :端口。";
                return false;
            }
            normalized = host + ":" + port;
            return true;
        }

        private static bool IsPlausibleHost(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 253) return false;
            if (host[0] == '.' || host[host.Length - 1] == '.') return false;
            bool previousDot = false;
            for (int i = 0; i < host.Length; i++)
            {
                char c = host[i];
                bool dot = c == '.';
                if (!(char.IsLetterOrDigit(c) || c == '-' || dot)) return false;
                if (dot && (previousDot || i == 0 || i == host.Length - 1)) return false;
                previousDot = dot;
            }
            return true;
        }

        private static string AddPort(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) return string.Empty;
            if (host.IndexOf(':') >= 0) return "[" + host + "]:" + port;
            return host + ":" + port;
        }

        private void CopyText(string value)
        {
            try
            {
                GUIUtility.systemCopyBuffer = value;
                localMessage = "已复制：" + value;
            }
            catch (Exception ex)
            {
                localMessage = "复制失败：" + ex.Message;
            }
        }

        private string LoadRecentAddress()
        {
            if (string.IsNullOrEmpty(settingsPath)) return string.Empty;
            try
            {
                if (!File.Exists(settingsPath)) return string.Empty;
                return File.ReadAllText(settingsPath).Trim();
            }
            catch { return string.Empty; }
        }

        private void SaveRecentAddress(string value)
        {
            if (string.IsNullOrEmpty(settingsPath)) return;
            try
            {
                string directory = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(settingsPath, value ?? string.Empty);
            }
            catch (Exception ex)
            {
                localMessage = "无法保存最近地址：" + ex.Message;
            }
        }

        private void DisableNativeUiInput()
        {
            Type uiCameraType = FindUiCameraType();
            if (uiCameraType == null) return;
            UnityEngine.Object[] found;
            try { found = Resources.FindObjectsOfTypeAll(uiCameraType); }
            catch { return; }
            for (int i = 0; i < found.Length; i++)
            {
                Behaviour behaviour = found[i] as Behaviour;
                if (behaviour == null || !behaviour.enabled || ContainsCamera(behaviour)) continue;
                disabledCameras.Add(behaviour);
                behaviour.enabled = false;
            }
        }

        private void RestoreNativeUiInput()
        {
            for (int i = disabledCameras.Count - 1; i >= 0; i--)
            {
                Behaviour behaviour = disabledCameras[i];
                if (behaviour != null) behaviour.enabled = true;
            }
            disabledCameras.Clear();
        }

        private bool ContainsCamera(Behaviour target)
        {
            for (int i = 0; i < disabledCameras.Count; i++)
            {
                if (ReferenceEquals(disabledCameras[i], target)) return true;
            }
            return false;
        }

        private static Type FindUiCameraType()
        {
            Assembly[] assemblies;
            try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return null; }
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = null;
                try { type = assemblies[i].GetType("UICamera", false); }
                catch { }
                if (type != null && typeof(Behaviour).IsAssignableFrom(type)) return type;
            }
            return null;
        }

        private void EnsureSkin()
        {
            if (skinReady || Event.current == null || GUI.skin == null) return;
            skinReady = true;
            try
            {
                lobbySkin = UnityEngine.Object.Instantiate(GUI.skin) as GUISkin;
                ownsLobbySkin = lobbySkin != null;
                if (lobbySkin == null) lobbySkin = GUI.skin;
                lobbyFont = CreatePreferredFont();
                if (lobbyFont != null) lobbySkin.font = lobbyFont;

                Color ice = new Color(0.73f, 0.94f, 0.96f);
                Color pale = new Color(0.93f, 0.96f, 0.97f);
                Color subdued = new Color(0.61f, 0.68f, 0.73f);
                Texture2D dark = MakeTexture(new Color(0.055f, 0.075f, 0.095f, 0.99f));
                Texture2D card = MakeTexture(new Color(0.075f, 0.102f, 0.127f, 0.99f));
                Texture2D active = MakeTexture(new Color(0.13f, 0.21f, 0.25f, 1f));
                Texture2D button = MakeTexture(new Color(0.81f, 0.91f, 0.94f, 1f));
                Texture2D hover = MakeTexture(new Color(0.96f, 0.99f, 1f, 1f));
                Texture2D pressed = MakeTexture(new Color(0.66f, 0.84f, 0.88f, 1f));
                Texture2D field = MakeTexture(new Color(0.025f, 0.039f, 0.053f, 1f));
                Texture2D fieldFocused = MakeTexture(new Color(0.043f, 0.069f, 0.083f, 1f));
                Texture2D ghost = MakeTexture(new Color(0.090f, 0.126f, 0.150f, 1f));
                Texture2D ghostHover = MakeTexture(new Color(0.15f, 0.21f, 0.24f, 1f));
                Texture2D warn = MakeTexture(new Color(0.22f, 0.13f, 0.15f, 1f));
                Texture2D warnHover = MakeTexture(new Color(0.35f, 0.17f, 0.20f, 1f));

                StyleBackground(lobbySkin.window, dark);
                StyleBackground(lobbySkin.box, card);
                StyleBackground(lobbySkin.button, button, hover, pressed);
                StyleBackground(lobbySkin.textField, field, fieldFocused, fieldFocused);
                lobbySkin.label.normal.textColor = pale;
                lobbySkin.box.normal.textColor = pale;
                lobbySkin.window.normal.textColor = pale;
                lobbySkin.button.normal.textColor = new Color(0.05f, 0.12f, 0.16f);
                lobbySkin.button.hover.textColor = new Color(0.02f, 0.09f, 0.13f);
                lobbySkin.button.active.textColor = new Color(0.02f, 0.09f, 0.13f);
                lobbySkin.textField.normal.textColor = pale;
                lobbySkin.textField.focused.textColor = Color.white;
                lobbySkin.textField.active.textColor = Color.white;

                titleStyle = new GUIStyle(lobbySkin.label);
                titleStyle.fontSize = 34;
                titleStyle.fontStyle = FontStyle.Bold;
                titleStyle.normal.textColor = pale;
                subtitleStyle = new GUIStyle(lobbySkin.label);
                subtitleStyle.fontSize = 17;
                subtitleStyle.normal.textColor = ice;
                bodyStyle = new GUIStyle(lobbySkin.label);
                bodyStyle.fontSize = 15;
                bodyStyle.clipping = TextClipping.Clip;
                mutedStyle = new GUIStyle(bodyStyle);
                mutedStyle.normal.textColor = subdued;
                accentStyle = new GUIStyle(bodyStyle);
                accentStyle.fontSize = 13;
                accentStyle.normal.textColor = new Color(1f, 0.63f, 0.62f);
                accentStyle.wordWrap = true;
                smallStyle = new GUIStyle(lobbySkin.label);
                smallStyle.fontSize = 13;
                smallStyle.alignment = TextAnchor.MiddleCenter;
                primaryButtonStyle = new GUIStyle(lobbySkin.button);
                primaryButtonStyle.fontSize = 17;
                primaryButtonStyle.fontStyle = FontStyle.Bold;
                primaryButtonStyle.alignment = TextAnchor.MiddleCenter;
                tabStyle = new GUIStyle(lobbySkin.button);
                tabStyle.fontSize = 14;
                textFieldStyle = new GUIStyle(lobbySkin.textField);
                textFieldStyle.fontSize = 19;
                textFieldStyle.padding = new RectOffset(14, 12, 11, 8);
                cardStyle = new GUIStyle(lobbySkin.box);
                sectionStyle = new GUIStyle(lobbySkin.label);
                sectionStyle.fontSize = 25;
                sectionStyle.fontStyle = FontStyle.Bold;
                eyebrowStyle = new GUIStyle(lobbySkin.label);
                eyebrowStyle.fontSize = 11;
                eyebrowStyle.normal.textColor = ice;
                eyebrowStyle.clipping = TextClipping.Clip;
                hintStyle = new GUIStyle(lobbySkin.label);
                hintStyle.fontSize = 12;
                hintStyle.normal.textColor = subdued;
                hintStyle.wordWrap = true;
                navStyle = new GUIStyle(lobbySkin.button);
                navStyle.fontSize = 15;
                navStyle.clipping = TextClipping.Clip;
                navStyle.alignment = TextAnchor.MiddleLeft;
                navStyle.padding = new RectOffset(13, 5, 0, 0);
                navStyle.normal.background = ghost;
                navStyle.hover.background = ghostHover;
                navStyle.active.background = active;
                navStyle.normal.textColor = subdued;
                navStyle.hover.textColor = pale;
                navStyle.active.textColor = pale;
                navActiveStyle = new GUIStyle(navStyle);
                navActiveStyle.normal.background = active;
                navActiveStyle.normal.textColor = ice;
                secondaryButtonStyle = new GUIStyle(navStyle);
                secondaryButtonStyle.fontSize = 13;
                secondaryButtonStyle.alignment = TextAnchor.MiddleCenter;
                secondaryButtonStyle.padding = new RectOffset(7, 7, 0, 0);
                dangerButtonStyle = new GUIStyle(secondaryButtonStyle);
                dangerButtonStyle.normal.background = warn;
                dangerButtonStyle.hover.background = warnHover;
                dangerButtonStyle.normal.textColor = new Color(0.99f, 0.79f, 0.79f);
                dangerButtonStyle.hover.textColor = Color.white;
                endpointStyle = new GUIStyle(bodyStyle);
                endpointStyle.fontSize = 14;
                endpointStyle.normal.textColor = pale;
                endpointStyle.clipping = TextClipping.Clip;
                statusStyle = new GUIStyle(bodyStyle);
                statusStyle.normal.textColor = new Color(0.63f, 0.95f, 0.85f);
            }
            catch
            {
                lobbySkin = GUI.skin;
                titleStyle = new GUIStyle(GUI.skin.label);
                subtitleStyle = new GUIStyle(GUI.skin.label);
                bodyStyle = new GUIStyle(GUI.skin.label);
                mutedStyle = new GUIStyle(GUI.skin.label);
                accentStyle = new GUIStyle(GUI.skin.label);
                smallStyle = new GUIStyle(GUI.skin.button);
                primaryButtonStyle = new GUIStyle(GUI.skin.button);
                tabStyle = new GUIStyle(GUI.skin.button);
                textFieldStyle = new GUIStyle(GUI.skin.textField);
                cardStyle = new GUIStyle(GUI.skin.box);
                sectionStyle = new GUIStyle(GUI.skin.label);
                eyebrowStyle = new GUIStyle(GUI.skin.label);
                hintStyle = new GUIStyle(GUI.skin.label);
                navStyle = new GUIStyle(GUI.skin.button);
                navActiveStyle = new GUIStyle(GUI.skin.button);
                secondaryButtonStyle = new GUIStyle(GUI.skin.button);
                dangerButtonStyle = new GUIStyle(GUI.skin.button);
                endpointStyle = new GUIStyle(GUI.skin.label);
                statusStyle = new GUIStyle(GUI.skin.label);
            }
        }

        private Font CreatePreferredFont()
        {
            string[] preferred = { "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑", "Arial" };
            try { return Font.CreateDynamicFontFromOSFont(preferred, 16); }
            catch
            {
                try { return Font.CreateDynamicFontFromOSFont("Arial", 16); }
                catch { return null; }
            }
        }

        private Texture2D MakeTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply();
            generatedTextures.Add(texture);
            return texture;
        }

        private static void StyleBackground(GUIStyle style, Texture2D normal)
        {
            StyleBackground(style, normal, normal, normal);
        }

        private static void StyleBackground(GUIStyle style, Texture2D normal, Texture2D hover, Texture2D active)
        {
            style.normal.background = normal;
            style.hover.background = hover;
            style.active.background = active;
            style.focused.background = hover;
            style.border = new RectOffset(0, 0, 0, 0);
        }
    }
}
