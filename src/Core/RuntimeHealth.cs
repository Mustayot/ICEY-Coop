using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public enum HealthSeverity
    {
        Info,
        Waiting,
        Pass,
        Warning,
        Failure
    }

    public sealed class HealthFinding
    {
        public string Code;
        public string Title;
        public string Evidence;
        public string Advice;
        public HealthSeverity Severity;
    }

    public sealed class RuntimeHealthSample
    {
        public string Role;
        public string State;
        public string Scene;
        public string LastError;
        public bool Active;
        public bool Loading;
        public bool PeerDetached;
        public bool CriticalPatchesOk;
        public bool SaveGuardArmed;
        public bool P1Bound;
        public bool P2Bound;
        public bool NativeInputActive;
        public bool ClientIsolationEnabled;
        public double ActiveSeconds;
        public double InputAgeSeconds;
        public double AckAgeSeconds;
        public double SnapshotAgeSeconds;
        public ulong InputAccepted;
        public ulong InputSent;
        public ulong InputAcked;
        public ulong SnapshotsReceived;
    }

    public sealed class RuntimeHealthReport
    {
        public List<HealthFinding> Findings = new List<HealthFinding>();
        public string Summary;
    }

    public static class RuntimeHealthEvaluator
    {
        public static RuntimeHealthReport Evaluate(RuntimeHealthSample s)
        {
            RuntimeHealthReport report = new RuntimeHealthReport();
            if (s == null)
            {
                Add(report, "sample", "采样无效", "未提供体检采样。", "重新读取运行状态。", HealthSeverity.Failure);
                AddCombatCaveat(report);
                Finish(report);
                return report;
            }

            string role = (s.Role ?? string.Empty).Trim().ToLowerInvariant();
            bool validRole = role == "offline" || role == "host" || role == "client";
            if (!validRole)
            {
                Add(report, "role", "角色类型无效", "Role 不属于 offline、host 或 client。", "修复运行采样中的角色字段。", HealthSeverity.Failure);
            }

            bool invalid = !Finite(s.ActiveSeconds) || s.ActiveSeconds < 0 ||
                !ValidAge(s.InputAgeSeconds) || !ValidAge(s.AckAgeSeconds) || !ValidAge(s.SnapshotAgeSeconds) ||
                (role == "client" && s.InputAcked > s.InputSent) ||
                (role == "host" && s.InputAccepted > 0 && s.InputAgeSeconds < 0) ||
                (role == "client" && s.InputAcked > 0 && s.AckAgeSeconds < 0) ||
                (role == "client" && s.SnapshotsReceived > 0 && s.SnapshotAgeSeconds < 0);
            if (invalid)
            {
                Add(report, "sample", "采样数据无效", "时长或通道计数不一致、为负数或非有限值。", "检查单调时钟和采样计数器；无效数据不作为通过证据。", HealthSeverity.Failure);
            }

            bool onlineRole = validRole && (role == "host" || role == "client");
            bool online = onlineRole && s.Active;
            if (!onlineRole)
            {
                Add(report, "patches", "关键补丁", "当前不是有效的联机角色。", "联机角色初始化后再检查。", HealthSeverity.Waiting);
                Add(report, "save-guard", "联机存档保护", "当前不是有效的联机角色。", "联机角色初始化后再检查。", HealthSeverity.Waiting);
            }
            else
            {
                Add(report, "patches", "关键补丁", s.CriticalPatchesOk ? "关键联机补丁均已安装。" : "一个或多个关键联机补丁未安装。", s.CriticalPatchesOk ? "继续观察实际联机样本。" : "检查补丁安装日志与绑定目标。", s.CriticalPatchesOk ? HealthSeverity.Pass : HealthSeverity.Failure);
                Add(report, "save-guard", "联机存档保护", s.SaveGuardArmed ? "联机存档保护已启用。" : "联机存档保护未启用。", s.SaveGuardArmed ? "继续观察存档保护状态。" : "停止联机写入并检查存档保护初始化。", s.SaveGuardArmed ? HealthSeverity.Pass : HealthSeverity.Failure);
            }
            if (!online)
            {
                Add(report, "binding", "角色绑定", "当前没有活动联机运行样本。", "进入可游玩场景后再检查角色绑定。", HealthSeverity.Waiting);
                Add(report, "input", "输入通道", "当前没有活动联机运行样本。", "联机活动后再产生输入样本。", HealthSeverity.Waiting);
                Add(report, "ack", "输入接收回执", "当前没有活动联机运行样本。", "联机活动后再检查主机接收回执。", HealthSeverity.Waiting);
                Add(report, "snapshot", "远端快照", "当前没有活动联机运行样本。", "联机活动后再检查远端快照。", HealthSeverity.Waiting);
            }
            else
            {
                if (s.Loading)
                {
                    Add(report, "binding", "角色绑定", "场景正在加载，绑定尚不可判定。", "等待场景加载完成后重新采样。", HealthSeverity.Waiting);
                }
                else if (s.ActiveSeconds > 3.0 && (!s.P1Bound || !s.P2Bound))
                {
                    Add(report, "binding", "角色绑定", "活动超过 3 秒后仍缺少" + (!s.P1Bound ? " P1" : "") + (!s.P2Bound ? " P2" : "") + "绑定。", "检查本机与远端角色绑定及场景初始化。", HealthSeverity.Warning);
                }
                else if (!s.P1Bound || !s.P2Bound)
                {
                    Add(report, "binding", "角色绑定", "角色绑定证据尚未齐全。", "等待角色绑定完成后重新采样。", HealthSeverity.Waiting);
                }
                else
                {
                    Add(report, "binding", "角色绑定", "需要的角色绑定已采到。", "继续观察后续运行样本。", HealthSeverity.Pass);
                }

                if (s.Loading)
                {
                    Add(report, "input", "输入通道", "场景正在加载，输入通道尚不可判定。", "等待场景加载完成后产生真实输入。", HealthSeverity.Waiting);
                    Add(report, "ack", "输入接收回执", "场景正在加载，回执通道尚不可判定。", "等待场景加载完成后再检查回执。", HealthSeverity.Waiting);
                    Add(report, "snapshot", "远端快照", "场景正在加载，快照尚不可判定。", "等待场景加载完成后重新采样。", HealthSeverity.Waiting);
                }
                else if (role == "client")
                {
                    if (!s.NativeInputActive)
                    {
                        Add(report, "input", "原生输入采样器", "原生输入采样器不可用。", "检查原生输入绑定。", HealthSeverity.Failure);
                    }
                    else if (s.InputSent == 0)
                    {
                        Add(report, "input", "原生输入采样器", "采样器可用，但尚无实际输入序号。", "产生真实游戏输入后检查新鲜度与主机回执。", HealthSeverity.Waiting);
                    }
                    else if (s.InputAgeSeconds >= 10.0)
                    {
                        Add(report, "input", "原生输入采样器", "最近输入样本已超过 10 秒。", "检查原生输入绑定与输入采样循环。", HealthSeverity.Failure);
                    }
                    else if (s.InputAgeSeconds > 3.0)
                    {
                        Add(report, "input", "原生输入采样器", "最近输入样本已超过 3 秒。", "产生新输入并检查输入采样循环。", HealthSeverity.Warning);
                    }
                    else
                    {
                        Add(report, "input", "原生输入采样器", "已采到新鲜输入序号 " + s.InputSent + "。", "输入样本不代表动作或伤害已执行。", HealthSeverity.Pass);
                    }
                    Add(report, "isolation", "客户端隔离", s.ClientIsolationEnabled ? "客户端隔离已启用。" : "客户端隔离未启用。", s.ClientIsolationEnabled ? "继续观察客户端行为。" : "检查客户端隔离补丁。", s.ClientIsolationEnabled ? HealthSeverity.Pass : HealthSeverity.Failure);
                    EvaluateClientAck(report, s);
                    EvaluateSnapshot(report, s);
                }
                else if (role == "host")
                {
                    if (s.InputAccepted == 0)
                    {
                        HealthSeverity severity = s.ActiveSeconds >= 5.0 ? HealthSeverity.Warning : HealthSeverity.Waiting;
                        Add(report, "input", "主机输入接收", "尚无主机接受序号。", "客户端产生输入后检查主机接受序号。", severity);
                    }
                    else if (s.InputAgeSeconds >= 10.0)
                    {
                        Add(report, "input", "主机输入接收", "最近一次主机接受序号已超过 10 秒。", "检查连接、输入发送端与主机接收循环。", HealthSeverity.Failure);
                    }
                    else if (s.InputAgeSeconds > 3.0)
                    {
                        Add(report, "input", "主机输入接收", "最近一次主机接受序号已超过 3 秒。", "检查连接与主机接收循环。", HealthSeverity.Warning);
                    }
                    else
                    {
                        Add(report, "input", "主机输入接收", "已采到主机接受序号 " + s.InputAccepted + "，距最近样本 " + s.InputAgeSeconds.ToString("0.0") + " 秒。", "序号仅证明主机接收，不代表动作或伤害已执行。", HealthSeverity.Pass);
                    }
                    Add(report, "ack", "输入接收回执", "主机角色没有客户端 ACK 通道。", "在客户端检查发送与主机回执。", HealthSeverity.Waiting);
                    Add(report, "snapshot", "远端快照", "主机角色不以接收远端快照作为此项判据。", "在客户端检查远端快照。", HealthSeverity.Waiting);
                }
            }

            if (s.PeerDetached)
            {
                Add(report, "peer", "远端连接", "远端角色已脱离。", "检查远端会话状态并重新连接。", HealthSeverity.Warning);
            }
            if (!string.IsNullOrEmpty(s.LastError) && s.LastError.Trim().Length > 0)
            {
                Add(report, "last-error", "最近错误", s.LastError.Trim(), "检查该错误对应的组件日志。", HealthSeverity.Warning);
            }
            AddCombatCaveat(report);
            if (invalid)
            {
                for (int i = 0; i < report.Findings.Count; i++)
                {
                    if (report.Findings[i].Severity == HealthSeverity.Pass)
                    {
                        report.Findings[i].Severity = HealthSeverity.Waiting;
                    }
                }
            }
            Finish(report);
            return report;
        }

        private static void EvaluateClientAck(RuntimeHealthReport report, RuntimeHealthSample s)
        {
            if (s.InputSent == 0)
            {
                Add(report, "ack", "输入接收回执", "尚未采到客户端输入发送序号。", "产生真实游戏输入后再检查回执。", HealthSeverity.Waiting);
            }
            else if (s.InputAcked == 0)
            {
                HealthSeverity severity = s.AckAgeSeconds >= 10.0 || s.ActiveSeconds >= 10.0 ? HealthSeverity.Failure :
                    s.AckAgeSeconds > 3.0 || s.ActiveSeconds >= 5.0 ? HealthSeverity.Warning : HealthSeverity.Waiting;
                Add(report, "ack", "输入接收回执", "已发送序号 " + s.InputSent + "，尚无主机回执序号。", "检查主机接收与 ACK 返回；ACK 不代表动作或伤害执行。", severity);
            }
            else if (s.AckAgeSeconds >= 10.0)
            {
                Add(report, "ack", "输入接收回执", "最近回执样本已超过 10 秒。", "产生新输入并检查回执链路。", HealthSeverity.Failure);
            }
            else if (s.AckAgeSeconds > 3.0)
            {
                Add(report, "ack", "输入接收回执", "最近回执样本已超过 3 秒。", "产生新输入并检查回执链路。", HealthSeverity.Warning);
            }
            else
            {
                Add(report, "ack", "输入接收回执", "主机已回执到序号 " + s.InputAcked + "（最近序号 " + s.InputSent + "），距最近回执 " + s.AckAgeSeconds.ToString("0.0") + " 秒。", "ACK 证明主机接收，不代表动作或伤害执行。", HealthSeverity.Pass);
            }
        }

        private static void EvaluateSnapshot(RuntimeHealthReport report, RuntimeHealthSample s)
        {
            if (s.Loading)
            {
                Add(report, "snapshot", "远端快照", "场景正在加载，快照尚不可判定。", "等待场景加载完成后重新采样。", HealthSeverity.Waiting);
                return;
            }
            if (s.SnapshotsReceived == 0)
            {
                HealthSeverity severity = s.SnapshotAgeSeconds >= 10.0 || (s.SnapshotAgeSeconds < 0 && s.ActiveSeconds >= 10.0) ? HealthSeverity.Failure :
                    s.ActiveSeconds >= 5.0 ? HealthSeverity.Warning : HealthSeverity.Waiting;
                Add(report, "snapshot", "远端快照", "尚未采到远端快照。", "等待或检查远端快照接收与场景同步。", severity);
            }
            else if (s.SnapshotAgeSeconds >= 10.0)
            {
                Add(report, "snapshot", "远端快照", "最近快照已超过 10 秒。", "检查快照发送、接收与场景同步。", HealthSeverity.Failure);
            }
            else if (s.SnapshotAgeSeconds > 3.0)
            {
                Add(report, "snapshot", "远端快照", "最近快照已超过 3 秒。", "检查快照发送、接收与场景同步。", HealthSeverity.Warning);
            }
            else
            {
                Add(report, "snapshot", "远端快照", "已采到远端快照，距最近样本 " + s.SnapshotAgeSeconds.ToString("0.0") + " 秒。", "快照证据不代表战斗结果正确。", HealthSeverity.Pass);
            }
        }

        private static void AddCombatCaveat(RuntimeHealthReport report)
        {
            Add(report, "combat-evidence", "伤害/技能需要实际样本", "尚未取得战斗实际 HP 变化或伤害结果证据。", "需要真实战斗样本后才能评估伤害与技能结果。", HealthSeverity.Info);
        }

        private static bool ValidAge(double value)
        {
            return Finite(value) && value >= -1.0;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void Add(RuntimeHealthReport report, string code, string title, string evidence, string advice, HealthSeverity severity)
        {
            report.Findings.Add(new HealthFinding { Code = code, Title = title, Evidence = evidence, Advice = advice, Severity = severity });
        }

        private static void Finish(RuntimeHealthReport report)
        {
            bool hasFailure = false;
            bool hasWarning = false;
            bool hasWaiting = false;
            for (int i = 0; i < report.Findings.Count; i++)
            {
                HealthSeverity severity = report.Findings[i].Severity;
                if (severity == HealthSeverity.Failure) hasFailure = true;
                else if (severity == HealthSeverity.Warning) hasWarning = true;
                else if (severity == HealthSeverity.Waiting) hasWaiting = true;
            }
            report.Summary = hasFailure ? "体检发现失败项。" :
                hasWarning ? "体检发现需要关注的项目。" :
                hasWaiting ? "部分通道正在等待实际样本。" : "已采到当前可评估项目。";
        }
    }
}
