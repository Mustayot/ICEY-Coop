using System.Reflection;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Network;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// T03 ownership/lifecycle probes. Components register their root (once) and
    /// attach themselves to that root so the registry never treats each ability
    /// as its own root. Enable/disable pairing is still counted.
    /// </summary>
    public static class T03ProbePatches
    {
        private static byte ResolveForComponent(Component c)
        {
            byte owner;
            if (BridgeContext.TryResolveOwnerByRoot(c, out owner))
            {
                return owner;
            }
            if (BridgeContext.ActiveCreation != null)
            {
                return BridgeContext.ActiveCreation.PlayerId;
            }
            return BridgeContext.CurrentCreationPlayerId;
        }

        private static byte ResolveForObject(object o)
        {
            byte owner;
            if (BridgeContext.Registry.TryResolveOwner(o, out owner))
            {
                return owner;
            }
            if (BridgeContext.ActiveCreation != null)
            {
                return BridgeContext.ActiveCreation.PlayerId;
            }
            return BridgeContext.CurrentCreationPlayerId;
        }

        public static void PostfixGameSceneInitStart(GameSceneInit __instance)
        {
            BridgeContext.CurrentCreationPlayerId = PlayerIds.P1;
            UnityEngine.GameObject p = UnityEngine.GameObject.FindGameObjectWithTag("Player");
            if (p != null)
            {
                BridgeContext.RegisterRoot(PlayerIds.P1, p);
                BridgeContext.P1Root = p;
            }
            BridgeContext.Info("GameSceneInit.Start: root registered owner=P1");
        }

        public static void PostfixPlayerAbilitiesAwake(PlayerAbilities __instance)
        {
            byte owner = ResolveForComponent(__instance);
            UnityEngine.GameObject root = __instance.transform.root.gameObject;
            BridgeContext.RegisterRoot(owner, root);
            bool ok = BridgeContext.Registry.Attach(__instance, root);
            BridgeContext.EnsureBinding(owner).Abilities = __instance;
            BridgeContext.Info(string.Format("PlayerAbilities.Awake: root={0} attached owner=P{1} ok={2}", root.name, owner, ok));
        }

        public static void PostfixPlayerAbilitiesOnEnable(PlayerAbilities __instance)
        {
            BridgeContext.NoteEnable();
        }

        public static void PostfixPlayerAbilitiesOnDisable(PlayerAbilities __instance)
        {
            BridgeContext.NoteDisable();
        }

        public static void PostfixCharacterStateInit(CharacterState __instance)
        {
            NativeActorBinding.BindState(__instance);
            byte owner = BridgeContext.ResolveOwner(__instance);

            if (BridgeContext.ActiveCreation != null)
            {
                if (BridgeContext.ActiveCreation.Root != null)
                {
                    bool ok = BridgeContext.Registry.Attach(__instance, BridgeContext.ActiveCreation.Root);
                    BridgeContext.Info(string.Format("CharacterState.Init: attached owner=P{0} ok={1}", owner, ok));
                }
                else
                {
                    BridgeContext.ActiveCreation.PendingObjects.Add(__instance);
                    BridgeContext.Info(string.Format("CharacterState.Init: pending owner=P{0} (root not ready)", owner));
                }
            }
            else if (BridgeContext.EnsureBinding(owner).Root != null)
            {
                bool ok = BridgeContext.Registry.Attach(__instance, BridgeContext.EnsureBinding(owner).Root);
                BridgeContext.Info(string.Format("CharacterState.Init: attached owner=P{0} ok={1}", owner, ok));
            }
            else
            {
                BridgeContext.Info(string.Format("CharacterState.Init: owner=P{0} no root to attach", owner));
            }
        }

        public static void PostfixPlayerInputAwake(PlayerInput __instance)
        {
            NativeActorBinding.BindAttribute(__instance);
            byte owner = ResolveForComponent(__instance);
            UnityEngine.GameObject root = __instance.transform.root.gameObject;
            BridgeContext.RegisterRoot(owner, root);
            bool ok = BridgeContext.Registry.Attach(__instance, root);
            BridgeContext.Info(string.Format("PlayerInput.Awake: root={0} attached owner=P{1} ok={2}", root.name, owner, ok));
        }

        public static void PostfixPlayerHurtAbilityOnEnable(PlayerHurtAbility __instance)
        {
            BridgeContext.NoteEnable();
            // PlayerHurtAbility is not a UnityEngine.Component (no transform),
            // so resolve via registry/creation scope and write it straight into
            // the binding; root/GetComponent cannot reach this type.
            byte owner = ResolveForObject(__instance);
            if (!PlayerIds.IsValid(owner))
            {
                BridgeContext.Info("PlayerHurtAbility.OnEnable: no owner resolved");
                return;
            }

            BridgeContext.SetHurtAbilityForOwner(owner, __instance);

            // Best-effort registry attach (root may not be ready yet) so the
            // PlayeAllHurt prefix can resolve this instance's owner.
            object root = null;
            if (BridgeContext.ActiveCreation != null && BridgeContext.ActiveCreation.Root != null)
            {
                root = BridgeContext.ActiveCreation.Root;
            }
            else if (owner == PlayerIds.P1 && BridgeContext.P1Root != null)
            {
                root = BridgeContext.P1Root;
            }

            if (root != null)
            {
                bool ok = BridgeContext.Registry.Attach(__instance, root);
                BridgeContext.Info(string.Format("PlayerHurtAbility.OnEnable: owner=P{0} bound ok=True attached ok={1}", owner, ok));
            }
            else
            {
                BridgeContext.Info(string.Format("PlayerHurtAbility.OnEnable: owner=P{0} bound ok=True (root not ready)", owner));
            }
        }

        public static void PostfixPlayerHurtAbilityOnDisable(PlayerHurtAbility __instance)
        {
            BridgeContext.NoteDisable();
        }

        public static void PostfixPlayerTimeControllerStart(PlayerTimeController __instance)
        {
            NativeActorBinding.BindAttribute(__instance);
            byte owner = ResolveForComponent(__instance);
            UnityEngine.GameObject root = __instance.transform.root.gameObject;
            BridgeContext.RegisterRoot(owner, root);
            bool ok = BridgeContext.Registry.Attach(__instance, root);
            BridgeContext.EnsureBinding(owner).TimeController = __instance;
            BridgeContext.Info(string.Format("PlayerTimeController.Start: root={0} attached owner=P{1} ok={2}", root.name, owner, ok));
        }


        /// <summary>
        /// P2 input path by role. Client samples and sends (SessionOrchestrator
        /// aggregates/sends) and never drives locally. Host drives P2 from the
        /// remote InputAdapter axis. Offline keeps the old scripted demo path.
        /// </summary>
        public static bool PrefixUpdateInput(PlayerInput __instance)
        {
            if (BridgeContext.IsClient)
            {
                // A client never drives its own actor. The native button state that the client
                // sampler reads is refreshed by GameWorld.Joystick.Update (UpdateRawKey /
                // UpdateGameInput), which runs on its own and is not suppressed here - so P2 input
                // still works while the native movement body is skipped.
                return false;
            }

            byte owner;
            if (!BridgeContext.Registry.TryResolveOwner(__instance, out owner) || owner != PlayerIds.P2)
            {
                return true;
            }

            if (BridgeContext.IsHost)
            {
                try
                {
                    PlayerMoveAbility move = GetMove(__instance);
                    if (move == null)
                    {
                        return false;
                    }
                    InputAdapter.TimeoutNeutral(PlayerIds.P2);
                    float axis = InputAdapter.ReadAxis(PlayerIds.P2, true);
                    int dir = axis > 0.2f ? 1 : (axis < -0.2f ? -1 : 0);
                    move.Move(dir);
                    DriveP2Abilities(__instance);
                    if (Time.frameCount % 300 == 0)
                    {
                        BridgeContext.Info(string.Format("[t13] p2 driven by network frame dir={0}", dir));
                    }
                }
                catch (System.Exception ex)
                {
                    BridgeContext.Error("[t17] remote actor update failed: " + ex.Message);
                }
                finally
                {
                    InputAdapter.CompleteActorTick(PlayerIds.P2);
                }
                return false;
            }

            // Offline demo path.
            try
            {
                PlayerMoveAbility move = GetMove(__instance);
                if (move == null)
                {
                    return false;
                }
                int dir = (Time.frameCount / 120) % 2 == 0 ? -1 : 1;
                move.Move(dir);
            }
            catch
            {
                // probe only; never break P2 update path
            }
            return false;
        }

        private static PlayerMoveAbility GetMove(PlayerInput __instance)
        {
            FieldInfo pabField = typeof(PlayerInput).GetField("pab",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object pab = pabField.GetValue(__instance);
            if (pab == null)
            {
                return null;
            }
            FieldInfo moveField = pab.GetType().GetField("move",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return (PlayerMoveAbility)moveField.GetValue(pab);
        }

        private static int comboLogs;
        /// <summary>True while the remote player holds the ordinary attack key for a charge.</summary>
        private static bool p2ChargeHeld;
        private static int chargeRequestLogs;
        private static int chargeRejectLogs;
        private static float lastComboLogAt = -1f;

        private static void DriveP2Abilities(PlayerInput __instance)
        {
            float axis = InputAdapter.ReadAxis(PlayerIds.P2, true);
            float vertical = InputAdapter.ReadAxis(PlayerIds.P2, false);
            // Keep the charge alive while the remote player holds the attack key: vanilla
            // drives PlayerChargingAbility.Charging() every frame, and the charge only
            // starts once every native gate lines up.
            if (p2ChargeHeld)
            {
                NativeChargingPatches.StartCharge(__instance);
            }
            if (axis < -0.2f || axis > 0.2f || vertical < -0.2f || vertical > 0.2f)
            {
                // Feed P2Anchor's stuck detector: someone is pressing a direction.
                BridgeContext.LastRemoteMoveTime = Time.realtimeSinceStartup;
            }
            int dir = axis < -0.2f ? -1 : (axis > 0.2f ? 1 : 3);
            foreach (RemoteActionKind action in InputAdapter.ConsumeActions(PlayerIds.P2))
            {
                string field, method;
                System.Type[] types = new System.Type[0];
                object[] args = new object[0];
                switch (action)
                {
                    case RemoteActionKind.Attack:
                    case RemoteActionKind.HeavyAttack:
                        field = "attack"; method = "PlayerAttack";
                        types = new System.Type[] { typeof(int), typeof(bool) };
                        args = new object[] { dir, action == RemoteActionKind.HeavyAttack };
                        // Combo diagnosis: vanilla chains light attacks inside
                        // PlayerAttackAbility. Log the arrival time so a run shows
                        // whether the presses arrive (and how far apart) when the
                        // remote player cannot chain.
                        if (comboLogs < 24)
                        {
                            comboLogs++;
                            BridgeContext.Info(string.Format("[t27] p2 attack arrived heavy={0} dir={1} t={2:F3} dt={3:F3}",
                                action == RemoteActionKind.HeavyAttack, dir,
                                Time.realtimeSinceStartup,
                                lastComboLogAt < 0f ? 0f : Time.realtimeSinceStartup - lastComboLogAt));
                            lastComboLogAt = Time.realtimeSinceStartup;
                        }
                        break;
                    case RemoteActionKind.Charge:
                        field = "attack"; method = "PlayerCirtPressAttack";
                        types = new System.Type[] { typeof(int) }; args = new object[] { dir };
                        break;
                    case RemoteActionKind.ChargeRelease:
                        field = "attack"; method = "PlayerCirtPressAttackReleasd"; break;
                    case RemoteActionKind.AtkCharge:
                        // Ordinary attack charge: the native gates (Enhancement.Charging,
                        // ChipManager.HasChipInScene, grounded, chargeable state) stay inside
                        // the game. Vanilla calls Charging() every frame while the key is
                        // held, so keep requesting it while our own hold flag is set - a
                        // single call made at the edge is rejected whenever P2 happens to be
                        // inside an attack state at that instant, and then the release finds
                        // no charge to release at all.
                        p2ChargeHeld = true;
                        if (NativeChargingPatches.StartCharge(__instance))
                        {
                            if (chargeRequestLogs < 6)
                            {
                                chargeRequestLogs++;
                                BridgeContext.Info("[t17] p2 charge requested gates="
                                    + NativeChargingPatches.Gates(__instance));
                            }
                        }
                        continue;
                    case RemoteActionKind.AtkChargeRelease:
                        p2ChargeHeld = false;
                        if (NativeChargingPatches.ReleaseCharge(__instance))
                        {
                            if (chargeRequestLogs < 10)
                            {
                                chargeRequestLogs++;
                                BridgeContext.Info("[t17] p2 ability invoked action=AtkChargeRelease"
                                    + " method=Claymore.ReleaseCharge");
                            }
                        }
                        else if (chargeRejectLogs < 8)
                        {
                            chargeRejectLogs++;
                            BridgeContext.Info("[t17] p2 charge release rejected state="
                                + NativeChargingPatches.CurrentStateName(__instance));
                        }
                        continue;
                    case RemoteActionKind.Jump:
                        field = "jump"; method = "Jump"; break;
                    case RemoteActionKind.Dash:
                        field = "flash";
                        string h = axis < -0.2f ? "Left" : (axis > 0.2f ? "Right" : "");
                        string v = vertical > 0.2f ? "Up" : (vertical < -0.2f ? "Down" : "");
                        method = h.Length + v.Length == 0 ? "FlashFace" : "Flash" + h + v;
                        break;
                    case RemoteActionKind.UpRising:
                        field = "upRising"; method = "UpJumpAttack"; break;
                    case RemoteActionKind.HitGround:
                        field = "hitGround"; method = "HitGround"; break;
                    case RemoteActionKind.Skill:
                        field = "skill"; method = "BladeStorm"; break;
                    case RemoteActionKind.SkillAlt:
                        field = "skill"; method = "ShadeAtk"; break;
                    case RemoteActionKind.Defence:
                        field = "defence"; method = "StartDefence"; break;
                    case RemoteActionKind.Execute:
                        // Vanilla: PlayerInput.UpdateInput does pab.execute.Execute()
                        // when Execute.OnPressed && Setting.CanExecute. The native
                        // PlayerExecuteAbility finds the willBeExecute enemy itself.
                        field = "execute"; method = "Execute"; break;
                    case RemoteActionKind.Interaction:
                        // Story interaction is a global static button
                        // (Core.Input.Game.Search.OnClick), not a P2 ability.
                        World.InteractionBridge.Request();
                        continue;
                    default: continue;
                }
                if (TriggerP2Ability(__instance, field, method, types, args)
                    && action != RemoteActionKind.Charge)
                    BridgeContext.Info("[t17] p2 ability invoked action=" + action + " method=" + method);
            }
        }
        private static bool TriggerP2Ability(PlayerInput __instance, string fieldName, string methodName, System.Type[] argTypes, object[] args)
        {
            try
            {
                FieldInfo pabField = typeof(PlayerInput).GetField("pab",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object pab = pabField.GetValue(__instance);
                if (pab == null)
                {
                    return false;
                }
                FieldInfo abilityField = pab.GetType().GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (abilityField == null)
                {
                    BridgeContext.Error("[t17] missing pab field " + fieldName);
                    return false;
                }
                object ability = abilityField.GetValue(pab);
                if (ability == null)
                {
                    BridgeContext.Error("[t17] pab field null " + fieldName);
                    return false;
                }
                System.Reflection.MethodInfo method = ability.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, argTypes, null);
                if (method == null)
                {
                    BridgeContext.Error("[t17] missing method " + methodName + " on " + fieldName);
                    return false;
                }
                method.Invoke(ability, args);
                return true;
            }
            catch (System.Exception ex)
            {
                BridgeContext.Error("[t17] ability trigger failed: " + ex.Message);
                return false;
            }
        }
    }
}
