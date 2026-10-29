namespace IceyCoop.Core
{
    /// <summary>
    /// Stable action ids shared by InputFrame.HeldBits bit positions and
    /// InputEdge.Action values. Correspond to native Core.Input.Game semantics;
    /// mapped by the GameBridge InputSampler/InputAdapter. V1 contract value
    /// table: both ends compile from this same source, so no protocol version
    /// bump is needed.
    /// </summary>
    public static class InputActions
    {
        public const int Attack = 0;   // 攻击（含蓄力）
        public const int Jump = 1;
        public const int Dash = 2;     // 闪避
        public const int Skill = 3;    // 技能
        public const int Defence = 4;  // 格挡
        public const int Up = 5;
        public const int Down = 6;
        /// <summary>
        /// 第二套技能（原生 ShadeAtk）。以前和 BladeStorm 合并成 Skill，
        /// 导致远端按两个技能键都只会触发 BladeStorm。
        /// </summary>
        public const int SkillAlt = 7;
        public const int CirtAttack = 8; // 原生重击 / 蓄力键
        public const int UpRising = 9;
        public const int HitGround = 10;
        /// <summary>
        /// 普通攻击的蓄力键（原生 Core.Input.Game.Charging）。原版里它和攻击共用同一个
        /// 物理键，长按 300ms 由 InputButtonProcessor.LongPressed 判定，所以客户端只采按下状态，
        /// 时长由 RemoteActionRouter 统一判定。
        /// </summary>
        public const int Charging = 11;
        /// <summary>
        /// 处决键（原生 Core.Input.Game.Execute，键盘是 L）。处决是瞬时动作，
        /// 只在按下沿触发，主机侧转成 PlayerExecuteAbility.Execute()。
        /// </summary>
        public const int Execute = 12;
        /// <summary>
        /// 交互/确认键（原生 Core.Input.Game.Search，键盘是回车）。剧情交互
        /// （打开强化、前往深处、进入仓房、存档点）都读 Search.OnClick。
        /// </summary>
        public const int Interaction = 13;
        public const int MaxHeld = 16;

        /// <summary>Trace/debug name for an action id; keeps both ends' logs
        /// greppable by the same token ("Attack", "Jump", ...).</summary>
        public static string NameOf(int action)
        {
            if (action == Attack)
            {
                return "Attack";
            }
            if (action == Jump)
            {
                return "Jump";
            }
            if (action == Dash)
            {
                return "Dash";
            }
            if (action == Skill)
            {
                return "Skill";
            }
            if (action == SkillAlt)
            {
                return "SkillAlt";
            }
            if (action == Defence)
            {
                return "Defence";
            }
            if (action == Up)
            {
                return "Up";
            }
            if (action == Down)
            {
                return "Down";
            }
            if (action == CirtAttack)
            {
                return "CirtAttack";
            }
            if (action == UpRising)
            {
                return "UpRising";
            }
            if (action == HitGround)
            {
                return "HitGround";
            }
            if (action == Charging)
            {
                return "Charging";
            }
            if (action == Execute)
            {
                return "Execute";
            }
            if (action == Interaction)
            {
                return "Interaction";
            }
            return "Action" + action;
        }
    }
}
