using System;
using System.Collections.Generic;
using System.Reflection;
using IceyCoop.Core;
using IceyCoop.Protocol;
using UnityEngine;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>
    /// Client-side input sampler. Reads the native Core.Input.Game buttons through
    /// reflection (the game's own Core namespace clashes with IceyCoop.Core), the
    /// physical button state even when the client's native gameplay gate is closed, and
    /// builds HeldBits + pressed/released edges with a monotonic ordinal.
    ///
    /// A missing native binding fails to neutral input. Synthetic movement is
    /// available only through the explicit ICEYCOOP_DEMO_INPUT=1 test option.
    /// </summary>
    public static class InputSampler
    {
        private static ulong nextSeq;
        private static float horizontal;
        private static float vertical;
        private static ushort heldBits;
        private static readonly List<InputEdge> edges = new List<InputEdge>();
        private static byte nextOrdinal;

        private static bool prevAtk;
        private static bool prevJump;
        private static bool prevDash;
        private static bool prevSkill;
        private static bool prevSkillAlt;
        private static bool prevDefence;
        private static bool prevUp;
        private static bool prevDown;
        private static bool prevCirtAttack;
        private static bool prevUpRising;
        private static bool prevHitGround;
        private static bool prevCharging;
        private static bool prevExecute;
        private static bool prevInteraction;

        /// <summary>Demo input mode (ICEYCOOP_DEMO_INPUT=1): alternate horizontal
        /// +/-1 every 120 frames.</summary>
        public static bool DemoMode;

        private static bool nativeVerified;
        private static bool nativeInputAvailable = true;
        private static bool demoAxisAnnounced;
        private static string nativeStatus = "unverified";
        private static int gateMismatchTraces;
        private static float lastDiagnostic;

        private sealed class ButtonBinding
        {
            public string Name;
            public object Target;
            public PropertyInfo Pressed;
            public FieldInfo RawPressed;
        }

        private static readonly Dictionary<string, ButtonBinding> bindings = new Dictionary<string, ButtonBinding>();
        private static readonly List<string> missingButtons = new List<string>();

        /// <summary>True when the sampler reads real native input; false when it is
        /// bindings are unavailable or explicit demo input is selected. This
        /// does not prove physical keys or gameplay gates are working.</summary>
        public static bool NativeInputActive
        {
            get { return !DemoMode && nativeInputAvailable && nativeVerified; }
        }

        public static string NativeStatus
        {
            get { return nativeStatus; }
        }

        public static void SampleLocal()
        {
            if (DemoMode)
            {
                AnnounceDemoAxis("ICEYCOOP_DEMO_INPUT=1");
                SampleDemoAxis();
                return;
            }

            if (!nativeInputAvailable)
            {
                SampleNeutral();
                return;
            }

            if (!nativeVerified)
            {
                VerifyNativeInput();
                if (!nativeInputAvailable)
                {
                    SampleNeutral();
                    return;
                }
            }

            try
            {
                bool moveRight = ButtonHeld("MoveRight");
                bool moveLeft = ButtonHeld("MoveLeft");
                horizontal = moveRight ? 1f : (moveLeft ? -1f : 0f);

                bool atk = ButtonHeld("Atk");
                bool jump = ButtonHeld("Jump");
                bool dash = ButtonHeld("Flash+Left") || ButtonHeld("Flash+Right")
                    || ButtonHeld("Flash+Up") || ButtonHeld("Flash+Down")
                    || ButtonHeld("Flash+LeftUp") || ButtonHeld("Flash+LeftDown")
                    || ButtonHeld("Flash+RightUp") || ButtonHeld("Flash+RightDown")
                    || ButtonHeld("Flash+FaceDirection");
                // Skills are a composite in vanilla: Game.BladeStorm = RawKey.L1 && RawKey.Square
                // (IL, UpdateGameInput), and RawKey.Pressed is gated by Core.Input.JoystickIsOpen,
                // which stays closed on a client - so the gamepad path can never assemble here.
                // The game's own key map is the other path, but a copy can be missing those
                // entries (GetCode falls back to KeyCode.None), which kills both at once. So:
                // the vanilla button, then the raw combo, then the shipped default keys.
                bool skill = IceyCoop.Core.InputComposition.Skill(ButtonHeld("BladeStorm"),
                    RawKeyHeld("L1"), RawKeyHeld("Square"), SkillDefaultKey(false));
                bool skillAlt = IceyCoop.Core.InputComposition.SkillAlt(ButtonHeld("ShadeAtk"),
                    RawKeyHeld("L1"), RawKeyHeld("Triangle"), SkillDefaultKey(true));
                bool defence = ButtonHeld("Defence");
                bool up = ButtonHeld("MoveUp");
                bool down = ButtonHeld("MoveDown");
                vertical = up == down ? 0f : (up ? 1f : -1f);
                bool cirtAttack = ButtonHeld("CirtAtk");
                // Use the native composites: Joystick may exclude Atk while a
                // directional attack combination is active.
                bool upRising = ButtonHeld("UpRising");
                bool hitGround = ButtonHeld("HitGround");
                // Core.Input.Game.Charging shares the physical attack key in vanilla
                // (IL 244363-244371); the 300ms LongPressed gate is applied by the
                // router so both ends agree on when a charge started.
                bool charging = ButtonHeld("Charging");
                // Execution is an instant action in vanilla (Execute.OnPressed),
                // so the sampler forwards it like the other extra actions.
                bool execute = ButtonHeld("Execute");
                // Story interaction (open upgrade / go deeper / enter room / save
                // point) reads Core.Input.Game.Search.OnClick inside scene triggers.
                bool interaction = ButtonHeld("Search");
                // An enhancement point is a local UI, not a world action: the press
                // edge opens the client's own panel and is NOT forwarded, otherwise
                // the host pops its panel on the host screen. Handled here instead of
                // AddExtraAction so a held key cannot re-trigger the edge every frame.
                bool interactionConsumed = false;
                if (interaction && !prevInteraction)
                {
                    interactionConsumed = World.PromptMirror.TryOpenEnhancementLocally();
                    if (!interactionConsumed)
                    {
                        AddEdge(edges, InputActions.Interaction, true, ref prevInteraction);
                    }
                }
                if (interaction)
                {
                    heldBits |= (ushort)(1 << InputActions.Interaction);
                }
                prevInteraction = interaction;

                heldBits = BuildHeldBits(atk, jump, dash, skill, skillAlt, defence, up, down);
                if (interaction)
                {
                    // BuildHeldBits rewrites heldBits, so re-apply the interaction bit.
                    heldBits |= (ushort)(1 << InputActions.Interaction);
                }
                edges.AddRange(BuildEdges(atk, jump, dash, skill, skillAlt, defence, up, down));
                AddExtraAction(InputActions.CirtAttack, cirtAttack, ref prevCirtAttack);
                AddExtraAction(InputActions.UpRising, upRising, ref prevUpRising);
                AddExtraAction(InputActions.HitGround, hitGround, ref prevHitGround);
                AddExtraAction(InputActions.Charging, charging, ref prevCharging);
                AddExtraAction(InputActions.Execute, execute, ref prevExecute);
                AdvancePrev(atk, jump, dash, skill, skillAlt, defence, up, down);
                if (Time.realtimeSinceStartup-lastDiagnostic >= 2f)
                {
                    lastDiagnostic=Time.realtimeSinceStartup;
                    BridgeContext.Info(string.Format("[input] scene={0} joystickOpen={1} leftRaw={2} rightRaw={3} axis={4} held={5} vertical={6}",
                        LevelManager.SceneName,global::Core.Input.JoystickIsOpen,
                        RawHeld("MoveLeft"),RawHeld("MoveRight"),horizontal,heldBits,vertical));
                    // Skill probe: ICEY derives the two skills from a gamepad-style combo
                    // (Game.BladeStorm = RawKey.L1 && RawKey.Square, ShadeAtk = L1 && Triangle
                    // - UpdateGameInput IL), so "the client cannot use its ultimate" can be
                    // either the combo never assembling here or the edge never arriving.
                    // These raw states say which.
                    BridgeContext.Info(string.Format("[input] skillprobe bs={0} sa={1} l1={2} square={3} triangle={4} cross={5} atk={6} k8={7} k9={8}",
                        RawHeld("BladeStorm"), RawHeld("ShadeAtk"),
                        RawKeyHeld("L1"), RawKeyHeld("Square"), RawKeyHeld("Triangle"),
                        RawKeyHeld("Cross"), RawHeld("Atk"),
                        SkillDefaultKey(false), SkillDefaultKey(true)));
                }
            }
            catch (Exception ex)
            {
                DegradeToDemoAxis("exception while reading native input: " + ex.Message);
                SampleNeutral();
            }
        }

        public static InputFrame BuildFrame()
        {
            if (nextSeq == ulong.MaxValue)
            {
                throw new InvalidOperationException("input seq overflow");
            }
            nextSeq++;
            InputFrame frame = new InputFrame();
            frame.InputSeq = nextSeq;
            frame.Horizontal = horizontal;
            frame.Vertical = vertical;
            frame.HeldBits = heldBits;
            int count = Math.Min(edges.Count, InputFrameCodec.MaxEdgesPerFrame);
            frame.Edges = edges.GetRange(0, count).ToArray();
            edges.RemoveRange(0, count);
            return frame;
        }

        public static ulong NextSeq
        {
            get { return nextSeq; }
        }

        private static void SampleDemoAxis()
        {
            horizontal = (Time.frameCount / 120) % 2 == 0 ? 1f : -1f;
            vertical = 0f;
            heldBits = 0;
            edges.Clear();
            AdvancePrev(false, false, false, false, false, false, false, false);
            prevCirtAttack = prevUpRising = prevHitGround = prevCharging = prevExecute = prevInteraction = false;
        }

        private static void SampleNeutral()
        {
            horizontal = vertical = 0f;
            heldBits = 0;
            edges.Clear();
            AdvancePrev(false, false, false, false, false, false, false, false);
            prevCirtAttack = prevUpRising = prevHitGround = prevCharging = prevExecute = prevInteraction = false;
        }

        private static void AnnounceDemoAxis(string reason)
        {
            if (demoAxisAnnounced)
            {
                return;
            }
            demoAxisAnnounced = true;
            BridgeContext.Info("[t17] input demo axis active reason=" + reason);
        }

        /// <summary>
        /// Resolves the native input surface once and reports the result loudly.
        /// Buttons are the exact Core.Input.Game / Core.Input.Game+Flash static
        /// field names read from the IL (Atk, Jump, Defence, BladeStorm, ShadeAtk,
        /// MoveLeft/Right/Up/Down and Flash.Left/Right).
        /// </summary>
        public static void VerifyNativeInput()
        {
            nativeVerified = true;
            missingButtons.Clear();

            Type game = Type.GetType("Core.Input+Game, Assembly-CSharp");
            Type flash = Type.GetType("Core.Input+Game+Flash, Assembly-CSharp");
            if (game == null)
            {
                DegradeToDemoAxis("type Core.Input+Game not found in Assembly-CSharp");
                return;
            }
            if (flash == null)
            {
                DegradeToDemoAxis("type Core.Input+Game+Flash not found in Assembly-CSharp");
                return;
            }

            string[] buttons = new string[]
            {
                "MoveLeft", "MoveRight", "MoveUp", "MoveDown",
                "Atk", "CirtAtk", "Charging", "UpRising", "HitGround", "Jump", "Defence", "BladeStorm", "ShadeAtk", "Execute", "Search",
                "Flash+Left", "Flash+Right", "Flash+Up", "Flash+Down",
                "Flash+LeftUp", "Flash+LeftDown", "Flash+RightUp", "Flash+RightDown",
                "Flash+FaceDirection"
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                ButtonBinding binding = ResolveButton(buttons[i]);
                if (binding == null)
                {
                    missingButtons.Add(buttons[i]);
                }
            }

            if (missingButtons.Count > 0)
            {
                string joined = string.Join(",", missingButtons.ToArray());
                BridgeContext.Error("[t17] input native reflection incomplete: missing buttons=" + joined);
                DegradeToDemoAxis("native buttons missing: " + joined);
                return;
            }

            nativeStatus = "native";
            BridgeContext.Info(string.Format("[t17] input native bindings resolved game=Core.Input+Game buttons={0}; gameplay gates still apply", buttons.Length));
            ReportInputSource();
        }

        /// <summary>
        /// One-shot diagnostic: Core.Input is refreshed by GameWorld.Joystick.Update, so "no keys
        /// ever seen" can mean the joystick object is missing/disabled rather than the player not
        /// pressing anything. Logged once per process so a manual run answers the question instead
        /// of requiring another experiment.
        /// </summary>
        private static void ReportInputSource()
        {
            try
            {
                Type joystickType = Type.GetType("GameWorld.Joystick, Assembly-CSharp");
                string joystick = "type-missing";
                if (joystickType != null)
                {
                    UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(joystickType);
                    if (found == null)
                    {
                        joystick = "absent";
                    }
                    else
                    {
                        Behaviour behaviour = found as Behaviour;
                        joystick = behaviour == null ? "present" : ("present enabled=" + behaviour.enabled);
                    }
                }
                string rewired = "unknown";
                if (joystickType != null)
                {
                    System.Reflection.FieldInfo player = joystickType.GetField("_player",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (player != null)
                    {
                        object value = player.GetValue(null);
                        rewired = value == null ? "null" : value.GetType().Name;
                    }
                    else
                    {
                        rewired = "field-missing";
                    }
                }
                BridgeContext.Info("[t17] input source: joystick=" + joystick + " rewiredPlayer=" + rewired
                    + " scene=" + LevelManager.SceneName);
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t17] input source diagnostic failed: " + ex.Message);
            }
        }

        private static void DegradeToDemoAxis(string reason)
        {
            nativeInputAvailable = false;
            nativeStatus = "degraded: " + reason;
            BridgeContext.Error("[t17] input native read FAILED: " + reason);
            BridgeContext.Error("[t17] native input unavailable; sending neutral input");
        }

        private static ushort BuildHeldBits(bool atk, bool jump, bool dash, bool skill, bool skillAlt, bool defence, bool up, bool down)
        {
            ushort bits = 0;
            if (atk) bits |= (ushort)(1 << InputActions.Attack);
            if (jump) bits |= (ushort)(1 << InputActions.Jump);
            if (dash) bits |= (ushort)(1 << InputActions.Dash);
            if (skill) bits |= (ushort)(1 << InputActions.Skill);
            if (skillAlt) bits |= (ushort)(1 << InputActions.SkillAlt);
            if (defence) bits |= (ushort)(1 << InputActions.Defence);
            if (up) bits |= (ushort)(1 << InputActions.Up);
            if (down) bits |= (ushort)(1 << InputActions.Down);
            return bits;
        }

        private static InputEdge[] BuildEdges(bool atk, bool jump, bool dash, bool skill, bool skillAlt, bool defence, bool up, bool down)
        {
            List<InputEdge> list = new List<InputEdge>();
            AddEdge(list, InputActions.Attack, atk, ref prevAtk);
            AddEdge(list, InputActions.Jump, jump, ref prevJump);
            AddEdge(list, InputActions.Dash, dash, ref prevDash);
            AddEdge(list, InputActions.Skill, skill, ref prevSkill);
            AddEdge(list, InputActions.SkillAlt, skillAlt, ref prevSkillAlt);
            AddEdge(list, InputActions.Defence, defence, ref prevDefence);
            AddEdge(list, InputActions.Up, up, ref prevUp);
            AddEdge(list, InputActions.Down, down, ref prevDown);
            return list.ToArray();
        }

        private static void AddEdge(List<InputEdge> list, int action, bool now, ref bool prev)
        {
            if (now == prev)
            {
                return;
            }
            InputEdge edge = new InputEdge();
            edge.Ordinal = nextOrdinal++;
            edge.Action = (byte)action;
            edge.Pressed = now;
            list.Add(edge);
            prev = now;
        }

        private static void AddExtraAction(int action, bool held, ref bool previous)
        {
            if (held) heldBits |= (ushort)(1 << action);
            AddEdge(edges, action, held, ref previous);
        }

        private static void AdvancePrev(bool atk, bool jump, bool dash, bool skill, bool skillAlt, bool defence, bool up, bool down)
        {
            prevAtk = atk;
            prevJump = jump;
            prevDash = dash;
            prevSkill = skill;
            prevSkillAlt = skillAlt;
            prevDefence = defence;
            prevUp = up;
            prevDown = down;
        }

        /// <summary>
        /// Reads a native button, preferring the physical key state.
        ///
        /// Vanilla's <c>Pressed</c> is <c>Core.Input.JoystickIsOpen &amp;&amp; Button.IsOpen &amp;&amp; _isPressed</c>
        /// (IL 241904). On the client both gates stay closed - the local character is not the one
        /// being driven, so the game opens neither the joystick nor the per-button gate - and the
        /// sampler forwarded nothing but zeros even while keys were held (live traces:
        /// <c>raw=True pressed=False</c>, and later <c>joystickOpen=False leftRaw=True axis=0</c>).
        /// Gating the fallback on <c>JoystickIsOpen</c> was not enough: the client keeps it closed.
        ///
        /// So the physical <c>_isPressed</c> is authoritative here. It is safe to trust: the trace
        /// shows it going True/False in step with the real keys. Sampling only runs while the
        /// session is Active (the orchestrator guards it), and what is forwarded is P2's input to
        /// the host, which still decides what to do with it.
        /// </summary>
        private static bool ButtonHeld(string name)
        {
            ButtonBinding binding;
            if (!bindings.TryGetValue(name, out binding))
            {
                binding = ResolveButton(name);
            }
            if (binding == null || binding.Target == null)
            {
                return false;
            }
            bool gated = false;
            if (binding.Pressed != null)
            {
                gated = (bool)binding.Pressed.GetValue(binding.Target, null);
            }
            bool raw = false;
            if (binding.RawPressed != null)
            {
                object rawValue = binding.RawPressed.GetValue(binding.Target);
                raw = rawValue != null && (bool)rawValue;
            }
            if (binding.Pressed == null && binding.RawPressed != null)
            {
                BridgeContext.Info("[t17] input button uses raw _isPressed (Pressed property missing) name=" + name);
            }
            if (!gated && raw && gateMismatchTraces < 4)
            {
                gateMismatchTraces++;
                BridgeContext.Info(string.Format(
                    "[t17] input gate mismatch button={0} raw=True pressed=False -> forwarding the physical key (client keeps the vanilla gates closed)",
                    name));
            }
            return gated || raw;
        }

        private static bool RawHeld(string name)
        {
            ButtonBinding binding;
            return bindings.TryGetValue(name,out binding) && binding.RawPressed != null &&
                (bool)binding.RawPressed.GetValue(binding.Target);
        }

        /// <summary>Shipped default skill keys (number 8 / 9, per the game's CreateDefaultMap).
        /// Read straight from the keyboard so a copy whose SettingData.KeyMap lost the
        /// skill entries still works; a remapped key is covered by ButtonHeld above.</summary>
        private static bool SkillDefaultKey(bool alt)
        {
            if (alt)
            {
                return UnityEngine.Input.GetKey(KeyCode.Alpha9) || UnityEngine.Input.GetKey(KeyCode.Keypad9);
            }
            return UnityEngine.Input.GetKey(KeyCode.Alpha8) || UnityEngine.Input.GetKey(KeyCode.Keypad8);
        }

        /// <summary>Raw state of a Core.Input.RawKey processor (the Rewired-backed layer
        /// the game composes its game buttons from). Diagnostics only.</summary>
        private static bool RawKeyHeld(string name)
        {
            try
            {
                Type t = Type.GetType("Core.Input+RawKey, Assembly-CSharp");
                if (t == null)
                {
                    return false;
                }
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (f == null)
                {
                    return false;
                }
                object target = f.GetValue(null);
                if (target == null)
                {
                    return false;
                }
                PropertyInfo pressed = target.GetType().GetProperty("Pressed");
                bool gated = pressed != null && (bool)pressed.GetValue(target, null);
                FieldInfo raw = target.GetType().GetField("_isPressed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                bool rawHeld = raw != null && (bool)raw.GetValue(target);
                return gated || rawHeld;
            }
            catch
            {
                return false;
            }
        }

        private static ButtonBinding ResolveButton(string name)
        {
            int plus = name.IndexOf('+');
            string typeName = plus >= 0 ? "Core.Input+Game+Flash" : "Core.Input+Game";
            string fieldName = plus >= 0 ? name.Substring(plus + 1) : name;

            Type t = Type.GetType(typeName + ", Assembly-CSharp");
            if (t == null)
            {
                BridgeContext.Error("[t17] input type missing: " + typeName);
                return null;
            }
            FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (f == null)
            {
                BridgeContext.Error("[t17] input field missing: " + typeName + "." + fieldName);
                return null;
            }
            object target = f.GetValue(null);
            if (target == null)
            {
                BridgeContext.Error("[t17] input button instance null: " + typeName + "." + fieldName);
                return null;
            }

            ButtonBinding binding = new ButtonBinding();
            binding.Name = name;
            binding.Target = target;
            binding.Pressed = target.GetType().GetProperty("Pressed", BindingFlags.Public | BindingFlags.Instance);
            binding.RawPressed = target.GetType().GetField("_isPressed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (binding.Pressed == null && binding.RawPressed == null)
            {
                BridgeContext.Error("[t17] input button has no Pressed/_isPressed: " + typeName + "." + fieldName);
                return null;
            }
            bindings[name] = binding;
            return binding;
        }
    }
}
