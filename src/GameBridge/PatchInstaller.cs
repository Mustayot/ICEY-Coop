using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Combat;
using IceyCoop.GameBridge.Diagnostics;
using IceyCoop.GameBridge.Presentation;
using IceyCoop.GameBridge.Progression;
using IceyCoop.GameBridge.Story;

namespace IceyCoop.GameBridge
{
    /// <summary>
    /// Applies Harmony patches from explicit bindings only. Every critical
    /// target is resolved with exact parameter types and checked before and
    /// after patching; any mismatch fails closed and rolls back this Harmony
    /// instance (including the save guard) so no partial patch set survives.
    /// </summary>
    public sealed class PatchInstaller
    {
        private const string HarmonyId = "io.iceycoop.gamebridge";

        /// <summary>
        /// Full critical set once installed: 2 save guard (Save+Delete),
        /// 10 T03 probes (9 postfix + 1 prefix), 3 move transpilers,
        /// 2 skill transpilers, 1 PlayerAction.Awake binding, 2 hurt routing
        /// (PlayeAllHurt + get_DamageCutOff), 5 weapon/anim/action (3 attribute
        /// transpilers + Reborn/Reset prefixes), 5 T10 entity (EnemyAttribute
        /// OnEnable/OnDisable + EnemyGenerator.GenerateEnemy +
        /// BattleCheckPointCollider.OnTriggerEnter2D + EnemyBaseHurt.EnemyDie),
        /// 4 T07 attack routing (PlayerAtk.EventTrigger + get_pAttr +
        /// GetFinalDamage + GetAtkLevel), 1 T08 ledger (EnemyBaseHurt.NormalHurt),
        /// 4 T09 execution (Claymore.HandleExecute + PlayerExecuteAbility.
        /// OnStateMachineStateTransfer + GetEmenies + PlayerExecuteTools.get_player;
        /// EnemyDie already counted in T10),
        /// 19 T11-A target (3 base getters + 16 BT/AI transpilers),
        /// 4 T11-B reward (AddCoinAndExp + RedMoney.OnTriggerStay2D +
        /// SupplyBoxAction.Update + ChildChipExplosion.OnEnable),
        /// 3 G25 death (PlayerHurtAbility.HpMinus + PlayerDie +
        /// LevelManager.OnPlayerDie),
        /// 3 T16 HUD/camera (UIDataBind.get_playerAttribute transpiler +
        /// CameraController.ZoomFinished + CameraZoomFinished postfixes),
        /// 2 T18 encounter (BattleCheckPoint.GenerateEnemyRushOneAfterOne +
        /// BattleCheckPoint.OpenArea postfixes),
        /// 3 T17 hurt outcome (EnemyBaseHurt.InDefense + CalSideStep + DoHurt)
        /// Base targets plus the exact owner-read and charging target sets.
        /// </summary>
        private const int CriticalPatchedMethods = 77 + NativeOwnerReadPatches.MethodCount
            + NativeChargingPatches.PatchedMethodCount;

        /// <summary>Client presentation-only set, counted via GetPatchedMethods
        /// on the whole Harmony instance: 2 SaveGuard (Save+Delete, installed in
        /// TryInstallAll) + 10 T03 read-only probes + 1 PlayerAction.Awake
        /// binding + 3 G25 death-intercept + 3 T16 HUD/camera + 3 binding methods = 22. No
        /// authoritative routing/ledger/entity/encounter/reward patches.</summary>
        private const int ClientPatchSetMethods = 23 + 1;   // + scene load gate (T19)

        private readonly TraceWriter log;
        private readonly BindingManifest manifest;
        private Harmony harmony;
        private bool installed;
        private bool criticalSetInstalled;

        public PatchInstaller(TraceWriter log, BindingManifest manifest)
        {
            this.log = log;
            this.manifest = manifest;
        }

        public bool IsInstalled
        {
            get { return installed; }
        }

        /// <summary>True only when save guard + T03 + T04 + T06 + T05 are all in.</summary>
        public bool IsCriticalSetInstalled
        {
            get { return criticalSetInstalled; }
        }

        public bool TryInstallAll(out string error)
        {
            error = null;
            try
            {
                if (installed)
                {
                    error = "already installed";
                    return false;
                }

                manifest.VerifyContentFingerprint();
                log.Info("content fingerprint ok");

                harmony = new Harmony(HarmonyId);
                ApplySaveGuard();
                VerifyPatched(2);

                installed = true;
                log.Info(string.Format("patches installed: {0}", PatchedCount()));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                if (harmony != null)
                {
                    try
                    {
                        harmony.UnpatchSelf();
                    }
                    catch
                    {
                        // best effort cleanup
                    }
                }
                installed = false;
                criticalSetInstalled = false;
                return false;
            }
        }

        public void UninstallAll()
        {
            if (harmony != null && installed)
            {
                harmony.UnpatchSelf();
                installed = false;
                criticalSetInstalled = false;
                log.Info("patches uninstalled");
            }
        }

        /// <summary>
        /// Critical probe/transpiler set, installed transactionally. Returns null
        /// on success or a context-bearing error string; on any failure the whole
        /// Harmony instance (save guard included) is rolled back and the caller
        /// must stay fail closed.
        /// </summary>
        public string InstallT03Probes()
        {
            if (harmony == null)
            {
                return "harmony not initialized";
            }
            try
            {
                if (BridgeContext.IsClient)
                {
                    InstallClientPatchSet();
                    // The death-box patch is optional, so the client count follows whether it was
                    // really applied instead of a hard-coded 24.
                    VerifyPatched(ClientPatchSetMethods + (G25DeathPatches.DeathBoxPatched ? 1 : 0) + Presentation.ReplicaAnimGuard.PatchedCount);
                    criticalSetInstalled = true;
                    log.Info(string.Format("client patch set installed: {0} methods", PatchedCount()));
                    return null;
                }

                ApplyProbe(typeof(GameSceneInit), "Start", "PostfixGameSceneInitStart");
                ApplyProbe(typeof(PlayerAbilities), "Awake", "PostfixPlayerAbilitiesAwake");
                ApplyProbe(typeof(PlayerAbilities), "OnEnable", "PostfixPlayerAbilitiesOnEnable");
                ApplyProbe(typeof(PlayerAbilities), "OnDisable", "PostfixPlayerAbilitiesOnDisable");
                ApplyProbe(typeof(CharacterState), "Init", "PostfixCharacterStateInit");
                ApplyProbe(typeof(PlayerInput), "Awake", "PostfixPlayerInputAwake");
                ApplyProbe(typeof(PlayerHurtAbility), "OnEnable", "PostfixPlayerHurtAbilityOnEnable");
                ApplyProbe(typeof(PlayerHurtAbility), "OnDisable", "PostfixPlayerHurtAbilityOnDisable");
                ApplyProbe(typeof(PlayerTimeController), "Start", "PostfixPlayerTimeControllerStart");
                ApplyPrefixProbe(typeof(PlayerInput), "UpdateInput", "PrefixUpdateInput");
                ApplyMoveTranspiler();
                NativeOwnerReadPatches.Install(harmony);
            NativeChargingPatches.Install(harmony);
                ApplySkillTranspilers();
                ApplyBindingTranspilers();
                ApplyHurtPatches();
                ApplyWeaponAnimPatches();
                ApplyEntityPatches();
                ApplyAttackRoutingPatches();
                ApplyLedgerPatches();
                ApplyExecutionPatches();
                ApplyTargetPatches();
                ApplyRewardPatches();
                G25DeathPatches.RegisterDeathPatches(harmony);
                ApplyHudCameraPatches();
                ApplySoftCollisionOwnerPatch();
            ApplySceneLoadGatePatch();
                StoryCapture.Install(harmony);
                CameraCueCapture.Install(harmony);
                EffectCueCapture.Install(harmony);
                World.InteractionBridge.Install(harmony);
                int interactionPatched = World.InteractionBridge.PatchedCount;
                storyPatched = StoryCapture.PatchedCount + CameraCueCapture.PatchedCount
                    + EffectCueCapture.PatchedCount + interactionPatched;
                // The death-box patch is optional (its type can be absent on another build), so the
                // expected count follows whether it was really applied instead of a hard-coded 93.
                VerifyPatched(CriticalPatchedMethods + sceneGatePatched
                    + (G25DeathPatches.DeathBoxPatched ? 1 : 0) + doorCentrePatched + storyPatched);

                criticalSetInstalled = true;
                log.Info(string.Format("critical patch set installed: {0} methods", PatchedCount()));
                return null;
            }
            catch (Exception ex)
            {
                try
                {
                    harmony.UnpatchSelf();
                }
                catch
                {
                    // best effort cleanup
                }
                installed = false;
                criticalSetInstalled = false;
                try
                {
                    string detail = ex.ToString();
                    if (ex.InnerException != null)
                    {
                        detail = detail + " || INNER: " + ex.InnerException.ToString();
                    }
                    detail = detail.Replace("\r", " ").Replace("\n", " | ");
                    log.Error("critical patch install detail: " + detail);
                }
                catch
                {
                    // diagnostics must never mask the original failure
                }
                return "critical patch install failed: " + ex.Message;
            }
        }

        private void InstallClientPatchSet()
        {
            // Presentation-only set: read probes + binding + death intercept.
            // Any authoritative install on a client must fail closed, so the
            // authoritative Apply* helpers are simply not reachable from here.
            ApplyProbe(typeof(GameSceneInit), "Start", "PostfixGameSceneInitStart");
            ApplyProbe(typeof(PlayerAbilities), "Awake", "PostfixPlayerAbilitiesAwake");
            ApplyProbe(typeof(PlayerAbilities), "OnEnable", "PostfixPlayerAbilitiesOnEnable");
            ApplyProbe(typeof(PlayerAbilities), "OnDisable", "PostfixPlayerAbilitiesOnDisable");
            ApplyProbe(typeof(CharacterState), "Init", "PostfixCharacterStateInit");
            ApplyProbe(typeof(PlayerInput), "Awake", "PostfixPlayerInputAwake");
            ApplyProbe(typeof(PlayerHurtAbility), "OnEnable", "PostfixPlayerHurtAbilityOnEnable");
            ApplyProbe(typeof(PlayerHurtAbility), "OnDisable", "PostfixPlayerHurtAbilityOnDisable");
            ApplyProbe(typeof(PlayerTimeController), "Start", "PostfixPlayerTimeControllerStart");
            ApplyPrefixProbe(typeof(PlayerInput), "UpdateInput", "PrefixUpdateInput");
            ApplyBindingTranspilers();
            G25DeathPatches.RegisterDeathPatches(harmony);
            ApplyHudCameraPatches();
            ApplyEnemyIsolationPatch();
            // Replicas must not run the enemies' own DestroySelf handlers: they switch the
            // GameObject off from the animation timeline, which made "some enemies never
            // show" invisible in every other diagnostic.
            Presentation.ReplicaAnimGuard.Install(harmony);
            // T19: the peer must not change scenes on its own; only the session may.
            ApplySceneLoadGatePatch();
        }

        /// <summary>
        /// Client-only: gate every local enemy at EnemyAttribute.OnEnable (the one
        /// anchor all spawn paths share, IL 214951-214979). See
        /// Actors/ClientIsolationPatches for why this is the right seam.
        /// </summary>
        private void ApplyEnemyIsolationPatch()
        {
            ApplyCombatPrefix(typeof(EnemyAttribute), "OnEnable", typeof(IceyCoop.GameBridge.Actors.ClientIsolationPatches), "PrefixEnemyAttributeOnEnable");
        }        /// <summary>
        /// Makes soft-collision aware of the second player: vanilla assumes any
        /// non-local impact belongs to an enemy and dereferences an EnemyAttribute
        /// that does not exist for P2 (see Combat/SoftCollisionOwnerPatch).
        /// </summary>
        private int sceneGatePatched;

        /// <summary>1 when the SceneGate.Enter door-centre patch was applied (host only).</summary>
        private int doorCentrePatched;

        /// <summary>Story side-effect capture hooks actually applied (host only, G7).</summary>
        private int storyPatched;

        /// <summary>
        /// T19: only the session may move the client to another gameplay scene. Every
        /// LevelManager.LoadScene(string) overload is gated.
        /// </summary>
        private void ApplySceneLoadGatePatch()
        {
            System.Collections.Generic.List<MethodInfo> methodList = AccessTools.GetDeclaredMethods(typeof(LevelManager));
            MethodInfo prefix = typeof(World.SceneLoadGate).GetMethod("PrefixLoadScene",
                BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                throw new MissingMethodException("[t19] missing prefix PrefixLoadScene");
            }
            int patched = 0;
            for (int i = 0; i < methodList.Count; i++)
            {
                MethodInfo m = methodList[i];
                if (!string.Equals(m.Name, "LoadScene", StringComparison.Ordinal)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                harmony.Patch(m, new HarmonyMethod(prefix), null);
                patched++;
            }
            sceneGatePatched = patched;
            log.Info(string.Format("[t19] scene load gate installed on {0} overload(s)", patched));

            // T19: remember which door was actually walked into so the assembly gate can measure
            // from the door instead of the host character. Host only: a client never runs the gate.
            doorCentrePatched = 0;
            if (!BridgeContext.IsClient)
            {
                Type sceneGate = AccessTools.TypeByName("SceneGate");
                if (sceneGate != null)
                {
                    MethodInfo enter = AccessTools.Method(sceneGate, "Enter", new Type[] { typeof(bool) });
                    MethodInfo doorPrefix = typeof(World.PartyAssemblyGate).GetMethod("PrefixSceneGateEnter",
                        BindingFlags.Public | BindingFlags.Static);
                    if (enter != null && doorPrefix != null)
                    {
                        harmony.Patch(enter, new HarmonyMethod(doorPrefix), null);
                        doorCentrePatched = 1;
                        log.Info("[t19] door centre patch installed on SceneGate.Enter(bool)");
                    }
                    else
                    {
                        log.Info("[t19] SceneGate.Enter(bool) not patchable; assembly centre stays the host character");
                    }
                }
            }
            if (patched == 0)
            {
                throw new MissingMethodException("[t19] LevelManager.LoadScene(string) not found");
            }
        }
        private void ApplySoftCollisionOwnerPatch()
        {
            MethodInfo target = AccessTools.Method(typeof(SoftCollidedImpact), "PlayerEnemyActive",
                new System.Type[] { typeof(SoftCollidedImpact) });
            if (target == null)
            {
                throw new MissingMethodException("[pc] missing target SoftCollidedImpact.PlayerEnemyActive");
            }
            MethodInfo prefix = typeof(SoftCollisionOwnerPatch).GetMethod("PrefixPlayerEnemyActive",
                BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                throw new MissingMethodException("[pc] missing prefix PrefixPlayerEnemyActive");
            }
            harmony.Patch(target, new HarmonyMethod(prefix), null);
            log.Info("[pc] soft-collision owner patch installed");
        }
        private void ApplyHudCameraPatches()
        {
            MethodInfo getPlayerAttribute = AccessTools.PropertyGetter(typeof(UIDataBind), "playerAttribute");
            if (getPlayerAttribute == null)
            {
                getPlayerAttribute = AccessTools.Method(typeof(UIDataBind), "get_playerAttribute");
            }
            if (getPlayerAttribute == null)
            {
                throw new MissingMethodException("[t16] missing target UIDataBind.get_playerAttribute");
            }
            MethodInfo transpiler = typeof(T16HudCameraPatches).GetMethod("TranspilerUIDataBindPlayerAttribute", BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException("[t16] missing transpiler TranspilerUIDataBindPlayerAttribute");
            }
            harmony.Patch(getPlayerAttribute, null, null, new HarmonyMethod(transpiler));

            ApplyCombatPostfix(typeof(CameraController), "ZoomFinished", typeof(T16HudCameraPatches), "PostfixZoomFinished");
            ApplyCombatPostfix(typeof(CameraController), "CameraZoomFinished", typeof(T16HudCameraPatches), "PostfixCameraZoomFinished");
            log.Info("[t16] HUD/camera patches installed");
        }

        private void ApplyProbe(System.Type type, string methodName, string postfixName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t03-probe] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo postfix = typeof(T03ProbePatches).GetMethod(postfixName, BindingFlags.Public | BindingFlags.Static);
            if (postfix == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t03-probe] missing postfix {0}", postfixName));
            }
            harmony.Patch(original, null, new HarmonyMethod(postfix));
        }

        private void ApplyPrefixProbe(System.Type type, string methodName, string prefixName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t03-probe] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo prefix = typeof(T03ProbePatches).GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t03-probe] missing prefix {0}", prefixName));
            }
            harmony.Patch(original, new HarmonyMethod(prefix), null);
        }

        private void ApplyMoveTranspiler()
        {
            ApplyTimeControllerTranspiler("Move", new System.Type[] { typeof(int) }, "TranspilerMove");
            ApplyTimeControllerTranspiler("PlayerMove", new System.Type[] { typeof(bool), typeof(float), typeof(Vector2), typeof(bool) }, "TranspilerPlayerMove");
            ApplyTimeControllerTranspiler("DealAirFric", new System.Type[] { typeof(float), typeof(bool) }, "TranspilerDealAirFric");
        }

        private void ApplySkillTranspilers()
        {
            ApplyEnhancementTranspiler("ShadeAtk", "TranspilerShadeAtk");
            ApplyEnhancementTranspiler("BladeStorm", "TranspilerBladeStorm");
        }

        private void ApplyEnhancementTranspiler(string methodName, string transpilerName)
        {
            MethodInfo original = AccessTools.Method(typeof(PlayerSkillAbility), methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t06] missing target PlayerSkillAbility.{0}", methodName));
            }
            MethodInfo transpiler = typeof(T06SkillPatches).GetMethod(transpilerName, BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t06] missing transpiler {0}", transpilerName));
            }
            harmony.Patch(original, null, null, new HarmonyMethod(transpiler));
            log.Info(string.Format("[t06] PlayerSkillAbility.{0} enhancement transpiler installed", methodName));
        }

        private void ApplyTimeControllerTranspiler(string methodName, System.Type[] parameterTypes, string transpilerName)
        {
            MethodInfo original = AccessTools.Method(typeof(PlayerMoveAbility), methodName, parameterTypes);
            if (original == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t04] missing target PlayerMoveAbility.{0}", methodName));
            }
            MethodInfo transpiler = typeof(T04MovePatches).GetMethod(transpilerName, BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException(string.Format(
                    "[t04] missing transpiler {0}", transpilerName));
            }
            harmony.Patch(original, null, null, new HarmonyMethod(transpiler));
            log.Info(string.Format("[t04] PlayerMoveAbility.{0} transpiler installed", methodName));
        }

        private void ApplyBindingTranspilers()
        {
            harmony.Patch(AccessTools.Method(typeof(PlayerAbilities), "Awake"),
                new HarmonyMethod(AccessTools.Method(typeof(NativeActorBinding), "PrefixAbilitiesAwake")), null,
                new HarmonyMethod(AccessTools.Method(typeof(NativeActorBinding), "TranspilerAbilitiesAwake")));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerAttribute), "platform"),
                new HarmonyMethod(AccessTools.Method(typeof(NativeActorBinding), "PrefixPlatform")));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerAttribute), "bounds"),
                new HarmonyMethod(AccessTools.Method(typeof(NativeActorBinding), "PrefixBounds")));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PlayerAttribute), "isInCharging"),
                new HarmonyMethod(AccessTools.Method(typeof(NativeActorBinding), "PrefixCharging")));
            MethodInfo original = AccessTools.Method(typeof(PlayerAction), "Awake");
            if (original == null)
            {
                throw new MissingMethodException("[t05] missing target PlayerAction.Awake");
            }
            MethodInfo transpiler = typeof(T05BindingPatches).GetMethod("TranspilerPlayerActionAwake", BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException("[t05] missing transpiler TranspilerPlayerActionAwake");
            }
            MethodInfo postfix = typeof(T05BindingPatches).GetMethod("PostfixPlayerActionAwake", BindingFlags.Public | BindingFlags.Static);
            if (postfix == null)
            {
                throw new MissingMethodException("[t05] missing postfix PostfixPlayerActionAwake");
            }
            harmony.Patch(original, null, new HarmonyMethod(postfix), new HarmonyMethod(transpiler));
            log.Info("[t05] PlayerAction.Awake binding patches installed");
        }

        private void ApplyHurtPatches()
        {
            MethodInfo playeAllHurt = AccessTools.Method(typeof(PlayerHurtAbility), "PlayeAllHurt",
                new Type[] { typeof(string), typeof(object), typeof(GameWorld.PlayerHurtAtkEventArgs) });
            if (playeAllHurt == null)
            {
                throw new MissingMethodException("[t07] missing target PlayerHurtAbility.PlayeAllHurt");
            }
            MethodInfo prefixPlaye = typeof(T07HurtPatches).GetMethod("PrefixPlayeAllHurt", BindingFlags.Public | BindingFlags.Static);
            if (prefixPlaye == null)
            {
                throw new MissingMethodException("[t07] missing prefix PrefixPlayeAllHurt");
            }
            harmony.Patch(playeAllHurt, new HarmonyMethod(prefixPlaye), null);

            MethodInfo transpilerPlayeEnhancement = typeof(T07HurtPatches).GetMethod("TranspilerPlayeAllHurtEnhancement", BindingFlags.Public | BindingFlags.Static);
            if (transpilerPlayeEnhancement == null)
            {
                throw new MissingMethodException("[t07] missing transpiler TranspilerPlayeAllHurtEnhancement");
            }
            harmony.Patch(playeAllHurt, null, null, new HarmonyMethod(transpilerPlayeEnhancement));

            MethodInfo damageCutOff = AccessTools.Method(typeof(PlayerHurtAbility), "get_DamageCutOff");
            if (damageCutOff == null)
            {
                throw new MissingMethodException("[t07] missing target PlayerHurtAbility.get_DamageCutOff");
            }
            MethodInfo transpilerDamageCutOff = typeof(T07HurtPatches).GetMethod("TranspilerDamageCutOff", BindingFlags.Public | BindingFlags.Static);
            if (transpilerDamageCutOff == null)
            {
                throw new MissingMethodException("[t07] missing transpiler TranspilerDamageCutOff");
            }
            harmony.Patch(damageCutOff, null, null, new HarmonyMethod(transpilerDamageCutOff));
            log.Info("[t07] hurt routing patches installed");
        }

        private void ApplyWeaponAnimPatches()
        {
            ApplyAttributeTranspiler(typeof(PlayerActionController), "Update", "TranspilerPlayerActionControllerUpdate");
            ApplyAttributeTranspiler(typeof(Claymore), "Start", "TranspilerClaymoreStart");
            ApplyAttributeTranspiler(typeof(PlayerAnimEventListener), "Start", "TranspilerAnimListenerStart");

            ApplyStaticVoidPrefix(typeof(PlayerAction), "Reborn", "PrefixPlayerActionReborn");
            harmony.Patch(AccessTools.Method(typeof(PlayerAction), "Reborn"), null,
                new HarmonyMethod(AccessTools.Method(typeof(T08WeaponAnimPatches), "PostfixPlayerActionReborn")));
            ApplyStaticVoidPrefix(typeof(PlayerAction), "Reset", "PrefixPlayerActionReset");
            log.Info("[t08] weapon/anim/action patches installed");
        }

        private void ApplyAttributeTranspiler(System.Type type, string methodName, string transpilerName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format("[t08] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo transpiler = typeof(T08WeaponAnimPatches).GetMethod(transpilerName, BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException(string.Format("[t08] missing transpiler {0}", transpilerName));
            }
            harmony.Patch(original, null, null, new HarmonyMethod(transpiler));
            log.Info(string.Format("[t08] {0}.{1} transpiler installed", type.FullName, methodName));
        }

        private void ApplyStaticVoidPrefix(System.Type type, string methodName, string prefixName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format("[t08] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo prefix = typeof(T08WeaponAnimPatches).GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                throw new MissingMethodException(string.Format("[t08] missing prefix {0}", prefixName));
            }
            harmony.Patch(original, new HarmonyMethod(prefix), null);
            log.Info(string.Format("[t08] {0}.{1} prefix installed", type.FullName, methodName));
        }

        private void ApplyTargetPatches()
        {
            ApplyPropertyGetterPrefix(typeof(EnemyBaseAction), "player", typeof(T11TargetPatches), "PrefixEnemyBaseActionGetPlayer");
            ApplyPropertyGetterPrefix(typeof(EnemyBaseHurt), "player", typeof(T11TargetPatches), "PrefixEnemyBaseHurtGetPlayer");
            ApplyPropertyGetterPrefix(typeof(EnemyBaseHurt), "pAttr", typeof(T11TargetPatches), "PrefixEnemyBaseHurtGetPAttr");

            ApplyTargetTranspiler("DaoBrotherAIAction+MoveToPlayer", "get_player", "TranspilerMoveToPlayerGetPlayer");
            ApplyTargetTranspiler("EnemyBaseAIAction+MoveAtkExAim", "get_player", "TranspilerMoveAtkExAimGetPlayer");
            ApplyTargetTranspiler("EnemyBaseAIAction+MoveAtkExAim", "OnUpdate", "TranspilerMoveAtkExAimOnUpdate");
            ApplyTargetTranspiler("EnemyBaseAIAction+MoveAtkExAim", "MoveAim", "TranspilerMoveAtkExAimMoveAim");
            ApplyTargetTranspiler("EnemyBaseAIAction+MoveAtkExAim", "MoveStop", "TranspilerMoveAtkExAimMoveStop");
            ApplyTargetTranspiler("EnemyBaseAIAction+FarAwayPlayer", "get_nearToPlayer", "TranspilerFarAwayPlayerGetNear");
            ApplyTargetTranspiler("EnemyBaseAIAction+FarAwayPlayer", "OnStart", "TranspilerFarAwayPlayerOnStart");
            ApplyTargetTranspiler("EnemyBaseAIAction+FarAwayPlayer", "JudeMoveDir", "TranspilerFarAwayPlayerJudeMoveDir");
            ApplyTargetTranspiler("EnemyBaseAIAction+RandomMovePlayer", "get_nearToPlayer", "TranspilerRandomMovePlayerGetNear");
            ApplyTargetTranspiler("EnemyBaseAIAction+RandomMovePlayer", "OnStart", "TranspilerRandomMovePlayerOnStart");
            ApplyTargetTranspiler("EnemyBaseAIAction+MoveToPlayerPos", "OnStart", "TranspilerMoveToPlayerPosOnStart");
            ApplyTargetTranspiler("PaoSisterAIAction+FarAwayPlayer", "get_player", "TranspilerPaoFarAwayGetPlayer");
            ApplyTargetTranspiler("EnemyBaseAIConditional+PlayerIsInAttackRange", "OnUpdate", "TranspilerPlayerIsInAttackRangeOnUpdate");
            ApplyTargetTranspiler("EnemyBaseAIConditional+InTwoAtkRange", "get_player", "TranspilerInTwoAtkRangeGetPlayer");
            ApplyTargetTranspiler("EnemyBaseAIConditional+DistanceJudge", "get_player", "TranspilerDistanceJudgeGetPlayer");
            ApplyTargetTranspiler("EnemyBaseAIConditional+IsInPlayerView", "get_player", "TranspilerIsInPlayerViewGetPlayer");
            log.Info("[t11] target patches installed");
        }

        private void ApplyRewardPatches()
        {
            ApplyCombatPrefix(typeof(EnemyBaseHurt), "AddCoinAndExp", typeof(T11RewardPatches), "PrefixAddCoinAndExp");
            ApplyCombatPrefix(typeof(RedMoney), "OnTriggerStay2D", typeof(T11RewardPatches), "PrefixRedMoneyOnTriggerStay2D");
            ApplyCombatPrefix(typeof(SupplyBoxAction), "Update", typeof(T11RewardPatches), "PrefixSupplyBoxActionUpdate");
            ApplyCombatPrefix(typeof(ChildChipExplosion), "OnEnable", typeof(T11RewardPatches), "PrefixChildChipExplosionOnEnable");
            log.Info("[t11] reward patches installed");
        }

        private void ApplyPropertyGetterPrefix(System.Type type, string propertyName, System.Type patchType, string prefixName)
        {
            MethodInfo getter = AccessTools.PropertyGetter(type, propertyName);
            if (getter == null)
            {
                getter = AccessTools.Method(type, "get_" + propertyName);
            }
            if (getter == null)
            {
                throw new MissingMethodException(string.Format("[t11] missing getter {0}.{1}", type.FullName, propertyName));
            }
            MethodInfo prefix = patchType.GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                throw new MissingMethodException(string.Format("[t11] missing prefix {0}", prefixName));
            }
            harmony.Patch(getter, new HarmonyMethod(prefix), null);
            log.Info(string.Format("[t11] {0}.{1} getter prefix installed", type.FullName, propertyName));
        }

        private void ApplyTargetTranspiler(string typeName, string methodName, string transpilerName)
        {
            System.Type type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                throw new MissingMethodException(string.Format("[t11] missing type {0}", typeName));
            }
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format("[t11] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo transpiler = typeof(T11TargetPatches).GetMethod(transpilerName, BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException(string.Format("[t11] missing transpiler {0}", transpilerName));
            }
            harmony.Patch(original, null, null, new HarmonyMethod(transpiler));
            log.Info(string.Format("[t11] {0}.{1} transpiler installed", type.FullName, methodName));
        }

        private void ApplyExecutionPatches()
        {
            MethodInfo getEnemies = AccessTools.Method(typeof(PlayerExecuteAbility), "GetEmenies", new Type[] { typeof(bool) });
            MethodInfo filterEnemies = AccessTools.Method(typeof(ExecutionCandidatePatches), "PostfixGetEnemies");
            if (getEnemies == null || filterEnemies == null)
                throw new MissingMethodException("[t09] missing execution candidate filter binding");
            harmony.Patch(getEnemies, null, new HarmonyMethod(filterEnemies));

            MethodInfo toolsPlayer = AccessTools.Method(typeof(PlayerExecuteTools), "get_player", Type.EmptyTypes);
            MethodInfo prefixPlayer = AccessTools.Method(typeof(T09ExecutionPatches), "PrefixExecuteToolsPlayer");
            if (toolsPlayer == null || prefixPlayer == null)
                throw new MissingMethodException("[t09] missing native execution owner binding");
            harmony.Patch(toolsPlayer, new HarmonyMethod(prefixPlayer), null);

            MethodInfo handleExecute = AccessTools.Method(typeof(Claymore), "HandleExecute",
                new Type[] { typeof(bool), typeof(EnemyAttribute) });
            if (handleExecute == null)
            {
                throw new MissingMethodException("[t09] missing target Claymore.HandleExecute");
            }
            MethodInfo prefixHandle = typeof(T09ExecutionPatches).GetMethod("PrefixHandleExecute", BindingFlags.Public | BindingFlags.Static);
            if (prefixHandle == null)
            {
                throw new MissingMethodException("[t09] missing prefix PrefixHandleExecute");
            }
            harmony.Patch(handleExecute, new HarmonyMethod(prefixHandle), null);

            MethodInfo transfer = AccessTools.Method(typeof(PlayerExecuteAbility), "OnStateMachineStateTransfer",
                new Type[] { typeof(object), typeof(StateMachine.TransferEventArgs) });
            if (transfer == null)
            {
                throw new MissingMethodException("[t09] missing target PlayerExecuteAbility.OnStateMachineStateTransfer");
            }
            MethodInfo postfixTransfer = typeof(T09ExecutionPatches).GetMethod("PostfixOnStateMachineStateTransfer", BindingFlags.Public | BindingFlags.Static);
            if (postfixTransfer == null)
            {
                throw new MissingMethodException("[t09] missing postfix PostfixOnStateMachineStateTransfer");
            }
            harmony.Patch(transfer, null, new HarmonyMethod(postfixTransfer));

            ApplyCombatPostfix(typeof(EnemyBaseHurt), "EnemyDie", typeof(T09ExecutionPatches), "PostfixEnemyDieCancelExecute");
            log.Info("[t09] execution patches installed");
        }

        private void ApplyLedgerPatches()
        {
            MethodInfo normalHurt = AccessTools.Method(typeof(EnemyBaseHurt), "NormalHurt",
                new Type[] { typeof(GameWorld.EnemyHurtAtkEventArgs.PlayerNormalAtkData), typeof(int), typeof(HurtCheck.BodyType), typeof(Vector2) });
            if (normalHurt == null)
            {
                throw new MissingMethodException("[t08] missing target EnemyBaseHurt.NormalHurt");
            }
            MethodInfo prefix = typeof(T08LedgerPatches).GetMethod("PrefixNormalHurt", BindingFlags.Public | BindingFlags.Static);
            MethodInfo postfix = typeof(T08LedgerPatches).GetMethod("PostfixNormalHurt", BindingFlags.Public | BindingFlags.Static);
            MethodInfo finalizer = typeof(T08LedgerPatches).GetMethod("FinalizerNormalHurt", BindingFlags.Public | BindingFlags.Static);
            if (prefix == null || postfix == null || finalizer == null)
            {
                throw new MissingMethodException("[t08] missing NormalHurt patch trio");
            }
            harmony.Patch(normalHurt, new HarmonyMethod(prefix), new HarmonyMethod(postfix), null, new HarmonyMethod(finalizer), null);
            log.Info("[t08] NormalHurt ledger patches installed");

            // T17 outcome capture: InDefense (block) / CalSideStep (dodge) /
            // DoHurt (damage applied) decide HitResolved.Outcome.
            ApplyCombatPostfix(typeof(EnemyBaseHurt), "InDefense", typeof(T08LedgerPatches), "PostfixInDefense");
            ApplyCombatPostfix(typeof(EnemyBaseHurt), "CalSideStep", typeof(T08LedgerPatches), "PostfixCalSideStep");
            ApplyCombatPostfix(typeof(EnemyBaseHurt), "DoHurt", typeof(T08LedgerPatches), "PostfixDoHurt");
            log.Info("[t17] hurt outcome capture patches installed");
        }

        private void ApplyAttackRoutingPatches()
        {
            MethodInfo eventTrigger = AccessTools.Method(typeof(PlayerAtk), "EventTrigger",
                new Type[] { typeof(GameWorld.EnemyHurtAtkEventArgs) });
            if (eventTrigger == null)
            {
                throw new MissingMethodException("[t07] missing target PlayerAtk.EventTrigger");
            }
            MethodInfo prefix = typeof(T07AttackPatches).GetMethod("PrefixEventTrigger", BindingFlags.Public | BindingFlags.Static);
            MethodInfo postfix = typeof(T07AttackPatches).GetMethod("PostfixEventTrigger", BindingFlags.Public | BindingFlags.Static);
            MethodInfo finalizer = typeof(T07AttackPatches).GetMethod("FinalizerEventTrigger", BindingFlags.Public | BindingFlags.Static);
            if (prefix == null || postfix == null || finalizer == null)
            {
                throw new MissingMethodException("[t07] missing EventTrigger patch trio");
            }
            harmony.Patch(eventTrigger, new HarmonyMethod(prefix), new HarmonyMethod(postfix), null, new HarmonyMethod(finalizer), null);

            ApplyDamageCalcTranspiler(typeof(PlayerDamageCalculate), "get_pAttr", "TranspilerGetPAttr");
            ApplyDamageCalcTranspiler(typeof(PlayerDamageCalculate), "GetFinalDamage", "TranspilerGetFinalDamage");
            ApplyDamageCalcTranspiler(typeof(HurtDataTools), "GetAtkLevel", "TranspilerGetAtkLevel");
            log.Info("[t07] attack routing patches installed");
        }

        private void ApplyDamageCalcTranspiler(System.Type type, string methodName, string transpilerName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format("[t07] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo transpiler = typeof(TDamageCalcPatches).GetMethod(transpilerName, BindingFlags.Public | BindingFlags.Static);
            if (transpiler == null)
            {
                throw new MissingMethodException(string.Format("[t07] missing transpiler {0}", transpilerName));
            }
            harmony.Patch(original, null, null, new HarmonyMethod(transpiler));
            log.Info(string.Format("[t07] {0}.{1} transpiler installed", type.FullName, methodName));
        }

        private void ApplyEntityPatches()
        {
            ApplyCombatPostfix(typeof(EnemyAttribute), "OnEnable", typeof(T10EntityPatches), "PostfixEnemyAttributeOnEnable");
            ApplyCombatPostfix(typeof(EnemyAttribute), "OnDisable", typeof(T10EntityPatches), "PostfixEnemyAttributeOnDisable");

            MethodInfo generate = AccessTools.Method(typeof(EnemyGenerator), "GenerateEnemy",
                new Type[] { typeof(EnemyGenerator.EnemyPrefab), typeof(Vector2), typeof(bool), typeof(bool) });
            if (generate == null)
            {
                throw new MissingMethodException("[t10] missing target EnemyGenerator.GenerateEnemy");
            }
            MethodInfo genPrefix = typeof(T10EntityPatches).GetMethod("PrefixGenerateEnemy", BindingFlags.Public | BindingFlags.Static);
            MethodInfo genPostfix = typeof(T10EntityPatches).GetMethod("PostfixGenerateEnemy", BindingFlags.Public | BindingFlags.Static);
            if (genPrefix == null || genPostfix == null)
            {
                throw new MissingMethodException("[t10] missing generate patch pair");
            }
            harmony.Patch(generate, new HarmonyMethod(genPrefix), new HarmonyMethod(genPostfix));

            ApplyCombatPrefix(typeof(BattleCheckPointCollider), "OnTriggerEnter2D", typeof(EncounterAdapter), "PrefixBattleCheckPointColliderOnTriggerEnter2D");
            ApplyCombatPostfix(typeof(EnemyBaseHurt), "EnemyDie", typeof(EncounterAdapter), "PostfixEnemyDie");

            MethodInfo rush = AccessTools.Method(typeof(BattleCheckPoint), "GenerateEnemyRushOneAfterOne",
                new Type[] { typeof(int), typeof(bool) });
            if (rush == null)
            {
                throw new MissingMethodException("[t18] missing target BattleCheckPoint.GenerateEnemyRushOneAfterOne");
            }
            MethodInfo rushPostfix = typeof(EncounterAdapter).GetMethod("PostfixGenerateEnemyRushOneAfterOne", BindingFlags.Public | BindingFlags.Static);
            if (rushPostfix == null)
            {
                throw new MissingMethodException("[t18] missing PostfixGenerateEnemyRushOneAfterOne");
            }
            MethodInfo rushPrefix = typeof(EncounterAdapter).GetMethod("PrefixGenerateEnemyRushOneAfterOne", BindingFlags.Public | BindingFlags.Static);
            if (rushPrefix == null)
            {
                throw new MissingMethodException("[t18] missing PrefixGenerateEnemyRushOneAfterOne");
            }
            harmony.Patch(rush, new HarmonyMethod(rushPrefix), new HarmonyMethod(rushPostfix));

            MethodInfo openArea = AccessTools.Method(typeof(BattleCheckPoint), "OpenArea");
            if (openArea == null)
            {
                throw new MissingMethodException("[t18] missing target BattleCheckPoint.OpenArea");
            }
            MethodInfo openPostfix = typeof(EncounterAdapter).GetMethod("PostfixOpenArea", BindingFlags.Public | BindingFlags.Static);
            if (openPostfix == null)
            {
                throw new MissingMethodException("[t18] missing PostfixOpenArea");
            }
            harmony.Patch(openArea, null, new HarmonyMethod(openPostfix));

            log.Info("[t10] entity/encounter patches installed");
        }

        private void ApplyCombatPostfix(System.Type type, string methodName, System.Type patchType, string postfixName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format("[t10] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo postfix = patchType.GetMethod(postfixName, BindingFlags.Public | BindingFlags.Static);
            if (postfix == null)
            {
                throw new MissingMethodException(string.Format("[t10] missing postfix {0}", postfixName));
            }
            harmony.Patch(original, null, new HarmonyMethod(postfix));
        }

        private void ApplyCombatPrefix(System.Type type, string methodName, System.Type patchType, string prefixName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                throw new MissingMethodException(string.Format("[t10] missing target {0}.{1}", type.FullName, methodName));
            }
            MethodInfo prefix = patchType.GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                throw new MissingMethodException(string.Format("[t10] missing prefix {0}", prefixName));
            }
            harmony.Patch(original, new HarmonyMethod(prefix), null);
        }

        public int PatchedCount()
        {
            if (harmony == null)
            {
                return 0;
            }
            return harmony.GetPatchedMethods().Count();
        }

        private void ApplySaveGuard()
        {
            Type saveDataType = AccessTools.TypeByName("SaveData");
            if (saveDataType == null)
            {
                throw new MissingMethodException("type not found: SaveData");
            }

            MethodInfo originalSave = AccessTools.Method(saveDataType, "Save",
                new Type[] { typeof(GameData), typeof(bool) });
            if (originalSave == null)
            {
                throw new MissingMethodException("SaveData.Save(GameData,bool) not found");
            }
            if (!originalSave.IsStatic || originalSave.ReturnType != typeof(bool))
            {
                throw new InvalidOperationException("SaveData.Save signature mismatch");
            }

            MethodInfo prefixSave = typeof(NativeSaveGuard).GetMethod("PrefixSave",
                BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(originalSave, new HarmonyMethod(prefixSave), null, null, null, null);

            MethodInfo originalDelete = AccessTools.Method(saveDataType, "Delete",
                new Type[] { typeof(bool) });
            if (originalDelete == null)
            {
                throw new MissingMethodException("SaveData.Delete(bool) not found");
            }
            if (!originalDelete.IsStatic || originalDelete.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("SaveData.Delete signature mismatch");
            }

            MethodInfo prefixDelete = typeof(NativeSaveGuard).GetMethod("PrefixDelete",
                BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(originalDelete, new HarmonyMethod(prefixDelete), null, null, null, null);
        }

        private void VerifyPatched(int expected)
        {
            int n = PatchedCount();
            if (n != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "expected exactly {0} patched methods, got {1}", expected, n));
            }
        }
    }
}
