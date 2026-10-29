using System;
using System.Globalization;
using System.IO;
using System.Text;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Network;
using IceyCoop.GameBridge.Progression;
using IceyCoop.GameBridge.World;
using UnityEngine;

namespace IceyCoop.GameBridge.Diagnostics
{
    /// <summary>Read-only main-thread observations. No synthetic input or game mutations.</summary>
    public sealed class RuntimeHealthMonitor : IDisposable
    {
        private readonly string directory;
        private readonly string runId;
        private readonly TraceWriter trace;
        private float nextSample;
        private bool visible;
        private bool disposed;
        private Vector2 scroll;
        private Font font;
        private GUIStyle textStyle;
        private GUIStyle buttonStyle;
        private string exportMessage = string.Empty;
        private DateTime capturedUtc;
        public RuntimeHealthSample CurrentSample { get; private set; }
        public RuntimeHealthReport CurrentReport { get; private set; }

        public RuntimeHealthMonitor(string gameRoot, TraceWriter writer)
        {
            directory = Path.Combine(gameRoot, "BepInEx/icey-coop/diagnostics");
            trace = writer;
            runId = Guid.NewGuid().ToString("N");
        }

        public void Tick(SessionOrchestrator session, bool patchesOk)
        {
            if (disposed) return;
            if (Input.GetKeyDown(KeyCode.F8)) visible = !visible;
            if (Time.realtimeSinceStartup < nextSample) return;
            nextSample = Time.realtimeSinceStartup + 1f;
            try { Capture(session, patchesOk); }
            catch (Exception ex)
            {
                // A diagnostic failure must not interrupt networking or native updates.
                exportMessage = "体检采样失败：" + ex.Message;
            }
        }

        private void Capture(SessionOrchestrator session, bool patchesOk)
        {
            RuntimeHealthSample sample = new RuntimeHealthSample();
            sample.Role = "Offline";
            sample.State = "Offline";
            sample.InputAgeSeconds = sample.AckAgeSeconds = sample.SnapshotAgeSeconds = -1;
            if (session != null) session.FillHealthSample(sample);
            sample.Scene = LevelManager.SceneName ?? string.Empty;
            sample.CriticalPatchesOk = patchesOk;
            sample.SaveGuardArmed = NativeSaveGuard.IsArmed;
            sample.PeerDetached = BridgeContext.PeerDetached;
            sample.NativeInputActive = InputSampler.NativeInputActive;
            sample.ClientIsolationEnabled = ClientIsolation.Enabled;
            NativePlayerBinding p1, p2;
            bool first = BridgeContext.TryGetBinding(PlayerIds.P1, out p1);
            bool second = BridgeContext.TryGetBinding(PlayerIds.P2, out p2);
            sample.P1Bound = sample.Role == "Client" ? ReplicaFactory.HasPlayerPresenter(PlayerIds.P1) : BindingReady(first ? p1 : null);
            sample.P2Bound = sample.Role == "Client" ? ReplicaFactory.HasPlayerPresenter(PlayerIds.P2) : BindingReady(second ? p2 : null);
            RuntimeHealthReport report = RuntimeHealthEvaluator.Evaluate(sample);
            int hp1 = 0, hp2 = 0, energy1 = 0, energy2 = 0;
            bool healthRead = false;
            if (sample.Role == "Client")
                healthRead = ReplicaFactory.TryReadPlayerHealth(PlayerIds.P1, out hp1, out energy1)
                    && ReplicaFactory.TryReadPlayerHealth(PlayerIds.P2, out hp2, out energy2);
            else if (sample.Role == "Host" && first && second && p1.Attribute != null && p2.Attribute != null)
            {
                hp1 = p1.Attribute.currentHP; energy1 = p1.Attribute.currentEnergy;
                hp2 = p2.Attribute.currentHP; energy2 = p2.Attribute.currentEnergy;
                healthRead = true;
            }
            if (healthRead)
                report.Findings.Add(new HealthFinding { Code = "PLAYER_HEALTH_READINGS", Severity = HealthSeverity.Info,
                    Title = sample.Role == "Client" ? "双方血量与能量（主机镜像）" : "双方血量与能量（主机原生）",
                    Evidence = "P1 HP=" + hp1 + " / 能量=" + energy1 + "；P2 HP=" + hp2 + " / 能量=" + energy2,
                    Advice = "这些是当前读数；独立攻击和受伤仍需实际战斗中的 HP 变化证据。" });
            if (!string.IsNullOrEmpty(BridgeContext.LastRuntimeError))
                report.Findings.Add(new HealthFinding { Code = "RUNTIME_ERROR_HISTORY", Severity = HealthSeverity.Info,
                    Title = "本次运行最近一条错误（历史）",
                    Evidence = BridgeContext.LastRuntimeErrorUtc.ToString("o") + " " + BridgeContext.LastRuntimeError,
                    Advice = "这是最近错误记录，可能发生在先前连接尝试；请结合时间和当前通道状态判断，不代表仍在发生。" });
            if (sample.Role == "Client" && session != null && session.PendingSnapshotEvents > 0)
                report.Findings.Add(new HealthFinding { Code = "SNAPSHOT_EVENT_LAG", Severity = HealthSeverity.Info,
                    Title = "新快照等待可靠事件", Evidence = "尚有 " + session.PendingSnapshotEvents + " 个主机事件未追平",
                    Advice = "这是两通道的顺序保护；持续无新快照将由快照时效项报警。保留双方日志检查事件积压。" });
            if (sample.Role == "Host" && first && second && p1.Attribute != null && p2.Attribute != null
                && (ReferenceEquals(p1.Attribute, p2.Attribute) || ReferenceEquals(p1.Root, p2.Root)
                    || (p1.Enhancement != null && ReferenceEquals(p1.Enhancement, p2.Enhancement))))
            {
                report.Findings.Add(new HealthFinding { Code = "OWNER_ALIAS", Severity = HealthSeverity.Failure,
                    Title = "两名玩家共享原生角色数据", Evidence = "P1/P2 Root、Attribute 或 Enhancement 存在同一引用",
                    Advice = "退出房间后保留双方日志；这是角色所有权异常，不能视为独立血量与技能正常。" });
                report.Summary = "发现角色所有权异常";
            }
            report.Findings.Add(new HealthFinding { Code = "INPUT_BINDING_DETAIL", Severity = HealthSeverity.Info,
                Title = "原生输入绑定", Evidence = InputSampler.NativeStatus ?? "unverified",
                Advice = "绑定存在不代表真人按键、技能或伤害已通过验证。" });
            CurrentSample = sample;
            CurrentReport = report;
            capturedUtc = DateTime.UtcNow;
        }

        private static bool BindingReady(NativePlayerBinding binding)
        {
            return binding != null && binding.Root != null && binding.Action != null
                && binding.Attribute != null && binding.Abilities != null;
        }

        public string Export()
        {
            if (CurrentReport == null || CurrentSample == null) throw new InvalidOperationException("尚无体检样本");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "health-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            // CreateNew prevents overwriting any existing user file, including a colliding name.
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(ReportJson());
            if (trace != null) trace.Info("[health] report exported run=" + runId + " summary=" + CurrentReport.Summary);
            return path;
        }

        private string ReportJson()
        {
            RuntimeHealthSample s = CurrentSample;
            StringBuilder b = new StringBuilder();
            b.Append("{\"schemaVersion\":1,\"capturedUtc\":").Append(Quote(capturedUtc.ToString("o")))
                .Append(",\"runId\":").Append(Quote(runId)).Append(",\"buildId\":").Append(Quote(BuildIdentity.Current))
                .Append(",\"role\":").Append(Quote(s.Role)).Append(",\"state\":").Append(Quote(s.State))
                .Append(",\"scene\":").Append(Quote(s.Scene)).Append(",\"summary\":").Append(Quote(CurrentReport.Summary))
                .Append(",\"inputAcceptedSeq\":").Append(s.InputAccepted).Append(",\"inputSentSeq\":").Append(s.InputSent)
                .Append(",\"inputAckedSeq\":").Append(s.InputAcked).Append(",\"snapshotsReceived\":").Append(s.SnapshotsReceived)
                .Append(",\"inputAgeSeconds\":").Append(Number(s.InputAgeSeconds))
                .Append(",\"ackAgeSeconds\":").Append(Number(s.AckAgeSeconds))
                .Append(",\"snapshotAgeSeconds\":").Append(Number(s.SnapshotAgeSeconds)).Append(",\"findings\":[");
            for (int i = 0; i < CurrentReport.Findings.Count; i++)
            {
                if (i > 0) b.Append(',');
                HealthFinding f = CurrentReport.Findings[i];
                b.Append("{\"code\":").Append(Quote(f.Code)).Append(",\"severity\":").Append(Quote(f.Severity.ToString()))
                    .Append(",\"title\":").Append(Quote(f.Title)).Append(",\"evidence\":").Append(Quote(f.Evidence))
                    .Append(",\"advice\":").Append(Quote(f.Advice)).Append('}');
            }
            return b.Append("]}").ToString();
        }

        private static string Number(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        public void Draw()
        {
            if (!visible || disposed) return;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUISkin oldSkin = GUI.skin;
            Color oldColor = GUI.color;
            try
            {
                float scale = Mathf.Max(0.25f, Mathf.Min(1f, Mathf.Min(Screen.width / 760f, Screen.height / 560f)));
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
                if (textStyle == null)
                {
                    textStyle = new GUIStyle(GUI.skin.label);
                    textStyle.wordWrap = true;
                    textStyle.fontSize = 15;
                    try { font = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "SimHei", "Arial" }, 15); textStyle.font = font; }
                    catch { }
                    buttonStyle = new GUIStyle(GUI.skin.button);
                    buttonStyle.font = textStyle.font;
                }
                GUI.Box(new Rect(20, 20, 710, 510), string.Empty);
                GUI.Label(new Rect(35, 23, 650, 25), "游戏联机体检  /  F8 关闭", textStyle);
                if (GUI.Button(new Rect(595, 48, 115, 30), "导出当前报告", buttonStyle))
                {
                    try { exportMessage = "已导出到 BepInEx/icey-coop/diagnostics"; Export(); }
                    catch (Exception ex) { exportMessage = "导出失败：" + ex.Message; }
                }
                if (CurrentSample == null || CurrentReport == null) { GUI.Label(new Rect(35, 60, 540, 60), "等待首个真实样本…", textStyle); return; }
                RuntimeHealthSample s = CurrentSample;
                GUI.Label(new Rect(35, 52, 550, 48), s.Role + " / " + s.State + " / " + s.Scene + "\n" + CurrentReport.Summary, textStyle);
                GUI.Label(new Rect(35, 105, 650, 45), "输入序号 接受/发送/确认：" + s.InputAccepted + "/" + s.InputSent + "/" + s.InputAcked
                    + "    快照：" + s.SnapshotsReceived + "\n这只是运行证据，不能替代真人玩法验收。", textStyle);
                float contentHeight = CurrentReport.Findings.Count * 105f;
                scroll = GUI.BeginScrollView(new Rect(32, 154, 680, 321), scroll, new Rect(0, 0, 652, contentHeight));
                for (int i = 0; i < CurrentReport.Findings.Count; i++)
                {
                    HealthFinding f = CurrentReport.Findings[i];
                    GUI.color = f.Severity == HealthSeverity.Failure ? new Color(1f, 0.55f, 0.5f)
                        : f.Severity == HealthSeverity.Warning ? new Color(1f, 0.85f, 0.5f) : Color.white;
                    GUI.Label(new Rect(4, i * 105, 638, 100), "[" + f.Severity + "] " + f.Title + "\n" + f.Evidence + "\n" + f.Advice, textStyle);
                }
                GUI.EndScrollView();
                GUI.color = Color.white;
                GUI.Label(new Rect(35, 483, 650, 40), exportMessage.Length == 0 ? "采样不暂停游戏。导出仅写入此游戏的 diagnostics 目录，不含重连凭证。" : exportMessage, textStyle);
            }
            finally { GUI.matrix = oldMatrix; GUI.skin = oldSkin; GUI.color = oldColor; }
        }

        public void Dispose()
        {
            disposed = true;
            if (font != null) UnityEngine.Object.Destroy(font);
        }
    }
}
