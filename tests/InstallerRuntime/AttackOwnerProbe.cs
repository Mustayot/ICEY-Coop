using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// T07 player->enemy attack attribution probe.
///
/// The room probe's damage is synthetic (it calls EnemyBaseHurt.HpMinus directly), so
/// PlayerAtk.EventTrigger - the single point where the native per-hit attackId and the
/// attacker are known - never fires in any existing probe. This probe instead:
///   1. enters a combat scene and walks P1 into the BattleCheckPoint trigger so the
///      game itself spawns wave 1,
///   2. teleports P2 next to a live enemy,
///   3. injects a real remote Attack frame through the production InputAdapter, so the
///      production dispatch drives P2's own PlayerAttackAbility,
///   4. counts PlayerAtk.EventTrigger per resolved owner (BridgeContext owner of the
///      hitting PlayerAtk instance) and asserts the hit was attributed to P2.
/// Nothing here calls damage helpers directly; the hit must come from P2's own attack.
/// </summary>
[BepInPlugin("io.iceycoop.attackownerprobe", "ICEYCoop Attack Owner Probe", "0.1.0")]
public sealed class AttackOwnerProbe : BaseUnityPlugin
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private enum Phase { LoadScene, Settle, OpenZone, WaitWave, PositionP2, Attack, P1Attack, AttrA, P1LowProbe, AttrB, P1HighProbe, LateHit, SplitPlatforms, DashSplit, BlockTest, DodgeTest, SkillIndep, HurtReal, CameraSettle, WipeTest, Verify, FallTest, GrowthLoop, Done }

    private static AttackOwnerProbe Current;

    private readonly StringBuilder report = new StringBuilder();
    private readonly Dictionary<byte, int> eventTriggerByOwner = new Dictionary<byte, int>();
    private int attackCalls;
    private int attackCallsP2;
    private Harmony harmony;
    private string output;
    private string scene;
    private Phase phase;
    private float startedAt;
    private float phaseAt;
    private float sceneReachedAt;
    private float sceneRequestAt;
    private object checkpoint;
    private object p2Root;
    private object p2AttackAbility;
    private object p1JumpAbility;
    private object p2JumpAbility;
    private int jumpCallsP1;
    private int jumpCallsP2;
    private bool jumpPatchOk;
    private object targetEnemy;
    private Vector3 enemyPosition;
    private int enemyHpBefore = -1;
    private int enemyHpAfter = -1;
    private ulong nextSeq;
    private int attackBursts;
    private float nextBurstAt;
    private float pendingReleaseAt;
    private float p1PressAt;
    private bool p1Pressed;
    private bool p1Positioned;
    private int p1PressedBursts;
    private int p2HpBefore = -1;
    private int p2HpAfter = -1;
    private int p1HpBefore = -1;
    private int p1HpAfter = -1;
    private Component attrTarget;
    private int attrLowHpBefore = -1;
    private int attrLowHpAfter = -1;
    private int attrHighHpBefore = -1;
    private int attrHighHpAfter = -1;
    private bool attrLowInit;
    private int p1LowHpBefore = -1;
    private int p1LowHpAfter = -1;
    private int p1HighHpBefore = -1;
    private int p1HighHpAfter = -1;
    private bool p1LowOwnedSample;
    private bool p1HighOwnedSample;
    private bool p1ProbeInit;
    private bool p1ProbeHeld;
    private float p1ProbeHeldAt;
    private float p1ProbeNextAt;
    private bool attrHighInit;
    private object savedAttackP2;
    private bool hurtInit;
    private int lastHurtReposition;
    private Vector3 cameraBaseline;
    private float cameraSizeBaseline = -1f;
    private bool cameraBaselineSet;
    private float cameraMaxDeviation;
    private float cameraMaxSizeDelta;
    private float cameraSettleEarly = -1f;
    private float cameraSettleLate = -1f;
    private float growthMinutes;
    private float growthStartedAt = -1f;
    private float growthNextSampleAt;
    private long growthFirstMem = -1;
    private int growthFirstEnemies = -1;
    private int growthFirstClones = -1;
    private long growthLastMem = -1;
    private int growthLastEnemies = -1;
    private int growthLastClones = -1;
    private int growthSamples;
    private string growthSwitchTo;
    private float growthSwitchAt = -1f;
    private bool growthSwitched;
    private int growthSceneSwitches;
    private float growthOpenZoneAt = -1f;
    private int growthZonesOpened;
    private int respawnStage;
    private float respawnStageAt;
    private int respawnBefore = -1;
    private int respawnAfter = -1;
    private int wipeStage;
    private float wipeStageAt;
    private int wipeWaveBefore = -1;
    private int wipeEnemiesAfter = -1;
    private string wipeWaveAfter;
    private bool wipeTestEnabled;
    private string wipeSceneBefore = "?";
    private string wipeSceneAfter = "?";
    private string wipeSwitchTo = "C2L1S7";
    private bool wipeSwitchDone;
    private bool wipeZoneFound;
    private int blockStage;
    private int blockConfirmDamage = -1;
    private int blockConfirmHp = -1;
    private float blockConfirmAt = -1f;
    private readonly List<int> p2AtkIds = new List<int>();
    private int lateStage;
    private int lateAtkStart;
    private int lateFirstAtkId;
    private int lateSecondAtkId;
    private int lateSettledBefore = -1;
    private ulong lateTargetId;
    private Component lateTarget;
    private MethodInfo normalHurtMethod;
    private bool hurtDeferTried;
    private bool vetoHookOk;
    private bool commitVetoed;
    private bool commitLogTried;
    private int commitLogs;
    private bool deferArmed;
    private bool deferredCaptured;
    private bool deferredReplaying;
    private object deferredInstance;
    private object deferredAtkData;
    private int deferredAtkId;
    private int deferredHolds;
    private object deferredKey;
    private int splitStage;
    private float splitStageAt;
    private float splitP1Y0;
    private float splitP2Y0;
    private float splitP1Peak;
    private float splitP2Peak;
    private int splitCallsP2Before;
    private int splitP2JumpBefore;
    private int splitSpotCount;
    private int splitJumpTries;
    private int splitPressTries;
    private float splitLastPressAt;
    private float splitHeightDiff;
    private int splitStableFrames;
    private float splitWaitStartedAt;
    private float splitWindowOverallAt;
    private float splitWindowStartedAt;
    private Vector3 splitPreviousP1;
    private Vector3 splitPreviousP2;
    private string splitPreviousP1State;
    private string splitPreviousP2State;
    private bool splitPreviousMotionValid;
    private int splitWindowAttempts;
    private int splitWindowP1Hp;
    private int splitWindowP2Hp;
    private int splitWindowAnchorResnaps;
    private int splitWindowP1JumpCalls;
    private int splitWindowP2JumpCalls;
    private string splitWindowP1State;
    private string splitWindowP2State;
    private bool splitWindowContaminated;
    private string splitWindowInvalidReason;
    private readonly HashSet<string> p1ActionStates = new HashSet<string>();
    private readonly HashSet<string> p2ActionStates = new HashSet<string>();
    private int bulletSamples;
    private int bulletLockedP1;
    private int bulletLockedP2;
    private int bulletLockedUnknown;
    private float bulletSampleAt = -1f;
    private object deferredBody;
    private object deferredHurtPos;
    private bool skillInit;
    private int skillE1Before = -1;
    private int skillE2Before = -1;
    private int skillE1After = -1;
    private int skillE2After = -1;
    private bool skillReleased;
    private float skillReleaseAt;
    private int skillStage;
    private int skillE1Control = -1;
    private float skillStageAt;
    private object p1SkillAbility;
    private object p2SkillAbility;
    private bool skillPatchTried;
    private bool stubPatchTried;
    private int consumedActionLogs;
    private int declaredBitLogs;
    private bool framePatchTried;
    private int frameLogs;
    private int droppedNeutralFrames;
    private bool surveyMode;
    private bool fallOnly;
    private bool executionCandidateOnly;
    private int dashStage;
    private float dashStageAt;
    private float dashP1X0;
    private float dashP2X0;
    private float dashP1Peak;
    private float dashP2Peak;
    private float dashP2ReleaseAt;
    private bool statePatchTried;
    private object p1Action;
    private object p2Action;
    private bool fallTestEnabled;
    private int fallStage;
    private float fallStageAt;
    private float fallStartY;
    private int fallHpBefore = -1;
    private int fallHpMin = -1;
    private bool fallFoundPit;
    private int skillCallCount;
    private bool skillDirectDone;
    private float skillDirectAt = -1f;
    private int skillAttempts;
    private float skillAttemptAt = -1f;
    private bool blockInit;
    private float PumpIntervalSeconds = 0.7f;
    private int blockHpBefore = -1;
    private int blockHpAfter = -1;
    private object savedMonsterDefence;
    private object savedHardLevel;
    private bool blockObserved;
    private bool dodgeInit;
    private bool dodgeObserved;
    private Component dodgeTarget;
    private int dodgeHpBefore = -1;
    private int dodgeHpAfter = -1;
    private int p1HurtBefore = -1;
    private int p1HurtAfter = -1;
    private int p2HurtBefore = -1;
    private int p2HurtAfter = -1;
    private int savedHp1 = -1;
    private int savedHp2 = -1;
    private int ledgerSettledBefore = -1;
    private int ledgerPendingBefore = -1;
    private int lastP2AtkId;
    private ulong clearedEnemyId;
    private readonly Dictionary<byte, int> outcomes = new Dictionary<byte, int>();
    private bool outcomePatchTried;
    private readonly Dictionary<string, object> savedAtkFields = new Dictionary<string, object>();
    private static readonly string[] AtkFields = new string[] { "Attack", "AvatarAttack", "Combo1", "Combo2",
        "UpperChop", "Knockout", "AirAttack", "AirAvatarAttack", "AirCombo1", "AirCombo2",
        "FlashAttack", "HitGround", "BladeStorm", "ShadeAttack", "TripleAttack", "Charging", "Chase" };
    private bool releaseSent;
    private int assertions;
    private int failures;
    private bool finished;
    private bool patchesOk;

    private void Awake()
    {
        Current = this;
        output = Environment.GetEnvironmentVariable("ICEYCOOP_PROBE_OUTPUT");
        if (string.IsNullOrEmpty(output))
        {
            output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "attack-owner.txt");
        }
        wipeTestEnabled = Environment.GetEnvironmentVariable("ICEYCOOP_WIPE_TEST") == "1";
        fallTestEnabled = Environment.GetEnvironmentVariable("ICEYCOOP_FALL_TEST") == "1";
        surveyMode = Environment.GetEnvironmentVariable("ICEYCOOP_SURVEY") == "1";
        fallOnly = Environment.GetEnvironmentVariable("ICEYCOOP_FALL_ONLY") == "1";
        executionCandidateOnly = Environment.GetEnvironmentVariable("ICEYCOOP_EXEC_CANDIDATE_ONLY") == "1";
        string switchTo = Environment.GetEnvironmentVariable("ICEYCOOP_GROWTH_SWITCH_TO");
        if (!string.IsNullOrEmpty(switchTo)) growthSwitchTo = switchTo;
        string switchAt = Environment.GetEnvironmentVariable("ICEYCOOP_GROWTH_SWITCH_AT");
        if (!string.IsNullOrEmpty(switchAt))
        {
            float at;
            if (float.TryParse(switchAt, out at) && at > 0f) growthSwitchAt = at;
        }
        string growthEnv = Environment.GetEnvironmentVariable("ICEYCOOP_GROWTH_MINUTES");
        if (!string.IsNullOrEmpty(growthEnv))
        {
            float parsed;
            if (float.TryParse(growthEnv, out parsed) && parsed > 0f) growthMinutes = parsed;
        }
        scene = Environment.GetEnvironmentVariable("ICEYCOOP_PROBE_SCENE");
        if (string.IsNullOrEmpty(scene))
        {
            scene = "C2L1S7";
        }
        startedAt = Time.realtimeSinceStartup;
        report.AppendLine("Attack owner probe role=host scene=" + scene);
        report.AppendLine("Method: real wave spawn + P2 teleported next to an enemy + production InputAdapter attack frame");
        if (executionCandidateOnly)
        {
            report.AppendLine("MODE execution-candidate-only: fixture diagnosis only; this is not a full attack-owner battle regression.");
        }
        try
        {
            InstallPatches();
        }
        catch (Exception ex)
        {
            report.AppendLine("PATCH_INSTALL_FAILED " + ex);
        }
        phase = Phase.LoadScene;
    }

    private void InstallPatches()
    {
        harmony = new Harmony("iceycoop.attackownerprobe");
        Type atk = FindType("PlayerAtk");
        MethodInfo eventTrigger = null;
        if (atk != null)
        {
            MethodInfo[] methods = atk.GetMethods(InstanceFlags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "EventTrigger")
                {
                    eventTrigger = methods[i];
                    break;
                }
            }
        }
        Type ability = FindType("PlayerAttackAbility");
        MethodInfo playerAttack = null;
        if (ability != null)
        {
            MethodInfo[] methods = ability.GetMethods(InstanceFlags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "PlayerAttack")
                {
                    playerAttack = methods[i];
                    break;
                }
            }
        }
        InstallOutcomePatch();
        InstallJumpAbilityPatch();
        if (eventTrigger != null)
        {
            harmony.Patch(eventTrigger, null,
                new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("EventTriggerPostfix", StaticFlags)));
        }
        if (playerAttack != null)
        {
            harmony.Patch(playerAttack, null,
                new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("PlayerAttackPostfix", StaticFlags)));
        }
        patchesOk = eventTrigger != null && playerAttack != null;
        report.AppendLine("patches: PlayerAtk.EventTrigger=" + (eventTrigger != null)
            + " PlayerAttackAbility.PlayerAttack=" + (playerAttack != null));
    }

    private void InstallJumpAbilityPatch()
    {
        Type jumpType = FindType("PlayerJumpAbility");
        MethodInfo jump = jumpType == null ? null : AccessTools.Method(jumpType, "Jump");
        if (jump == null) { report.AppendLine("PlayerJumpAbility.Jump was not found"); return; }
        harmony.Patch(jump, null, new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("JumpAbilityPostfix", StaticFlags)));
        jumpPatchOk = true;
        report.AppendLine("patched PlayerJumpAbility.Jump owner counter");
    }

    private static void JumpAbilityPostfix(object __instance)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished) return;
        if (probe.p1JumpAbility != null && ReferenceEquals(__instance, probe.p1JumpAbility)) probe.jumpCallsP1++;
        if (probe.p2JumpAbility != null && ReferenceEquals(__instance, probe.p2JumpAbility)) probe.jumpCallsP2++;
    }

    private void InstallFramePatch()
    {
        Type adapter = FindType("IceyCoop.GameBridge.Network.InputAdapter");
        MethodInfo apply = adapter == null ? null : adapter.GetMethod("ApplyRemoteInput", StaticFlags);
        if (apply == null) return;
        framePatchTried = true;
        harmony.Patch(apply, new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("ApplyFramePrefix", StaticFlags)));
        report.AppendLine("patched InputAdapter.ApplyRemoteInput");
    }

    private static bool ApplyFramePrefix(byte playerId, object frame)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished || playerId != 2) return true;
        // Single-sender experiment for the skill window: the client keeps streaming neutral frames
        // (no edges, no held bits) and each of them clears the adapter's pending edges - that is
        // exactly why an injected Skill press never survived. Dropping the neutral frames leaves the
        // probe's own edge-carrying frame as the only sender.
        if (probe.phase == Phase.SkillIndep)
        {
            object heldNow = Read(frame, "HeldBits");
            int heldBits = heldNow == null ? 0 : Convert.ToInt32(heldNow);
            System.Array edgeNow = Read(frame, "Edges") as System.Array;
            int edgeCount = edgeNow == null ? 0 : edgeNow.Length;
            if (edgeCount == 0 && heldBits == 0)
            {
                probe.droppedNeutralFrames++;
                return false;
            }
        }
        if (probe.frameLogs >= 30 && probe.phase != Phase.SkillIndep) return true;
        try
        {
            object seq = Read(frame, "InputSeq");
            object held = Read(frame, "HeldBits");
            object edges = Read(frame, "Edges");
            string edgeText = "";
            System.Array arr = edges as System.Array;
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    object e = arr.GetValue(i);
                    edgeText += "(" + Convert.ToString(Read(e, "Action")) + ","
                        + Convert.ToString(Read(e, "Pressed")) + ")";
                }
            }
            probe.frameLogs++;
            probe.report.AppendLine("frame in: seq=" + Convert.ToString(seq) + " held=" + Convert.ToString(held)
                + " edges=" + edgeText + " phase=" + probe.phase);
        }
        catch
        {
        }
        return true;
    }

    private void InstallConsumeActionsPatch()
    {
        Type adapter = FindType("IceyCoop.GameBridge.Network.InputAdapter");
        MethodInfo consume = adapter == null ? null : adapter.GetMethod("ConsumeActions", StaticFlags);
        if (consume == null) return;
        stubPatchTried = true;
        harmony.Patch(consume, null,
            new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("ConsumeActionsPostfix", StaticFlags)));
        report.AppendLine("patched InputAdapter.ConsumeActions");
    }

    private static void ConsumeActionsPostfix(byte playerId, object __result)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished || playerId != 2) return;
        if (__result == null) return;
        System.Collections.IEnumerable list = __result as System.Collections.IEnumerable;
        if (list == null) return;
        string joined = "";
        int count = 0;
        foreach (object item in list)
        {
            joined += Convert.ToString(item) + ",";
            count++;
        }
        // Log inside the skill window too, including empty results: "no line at all" and
        // "called but produced nothing" mean very different things here.
        bool inSkillWindow = probe.phase == Phase.SkillIndep;
        if (count > 0 && probe.consumedActionLogs < 40)
        {
            probe.consumedActionLogs++;
            probe.report.AppendLine("consumed actions: " + joined);
        }
        else if (inSkillWindow && probe.declaredBitLogs < 30)
        {
            probe.declaredBitLogs++;
            probe.report.AppendLine("consumed actions (skill window): count=" + count
                + " actions=" + joined + " t=" + F(Time.realtimeSinceStartup));
        }
    }

    private void InstallSkillPatch()
    {
        Type skill = FindType("PlayerSkillAbility");
        MethodInfo blade = null;
        if (skill != null)
        {
            MethodInfo[] methods = skill.GetMethods(InstanceFlags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "BladeStorm" && methods[i].GetParameters().Length == 0)
                {
                    blade = methods[i];
                    break;
                }
            }
        }
        if (blade == null) return;
        skillPatchTried = true;
        harmony.Patch(blade, new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("SkillPrefix", StaticFlags)));
        report.AppendLine("patched PlayerSkillAbility.BladeStorm");
    }

    private static void SkillPrefix(object __instance)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished) return;
        bool isP2 = probe.p2SkillAbility != null && object.ReferenceEquals(__instance, probe.p2SkillAbility);
        bool isP1 = probe.p1SkillAbility != null && object.ReferenceEquals(__instance, probe.p1SkillAbility);
        probe.skillCallCount++;
        probe.report.AppendLine("skill call: isP2Ability=" + isP2 + " isP1Ability=" + isP1
            + " energy p1=" + probe.PlayerEnergy(1) + " p2=" + probe.PlayerEnergy(2));
    }

    private void InstallOutcomePatch()
    {
        Type capture = FindType("IceyCoop.GameBridge.Combat.HurtOutcomeCapture");
        MethodInfo resolve = capture == null ? null : capture.GetMethod("Resolve", StaticFlags);
        if (resolve == null)
        {
            return;
        }
        outcomePatchTried = true;
        harmony.Patch(resolve, null,
            new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("ResolvePostfix", StaticFlags)));
        report.AppendLine("patched HurtOutcomeCapture.Resolve");
    }

    private static void ResolvePostfix(byte __result)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished)
        {
            return;
        }
        int count;
        probe.outcomes.TryGetValue(__result, out count);
        probe.outcomes[__result] = count + 1;
    }

    private static void EventTriggerPostfix(object __instance)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished || __instance == null)
        {
            return;
        }
        byte owner = 255;
        try
        {
            probe.TryResolveOwner(__instance, out owner);
        }
        catch
        {
            owner = 254;
        }
        int count;
        probe.eventTriggerByOwner.TryGetValue(owner, out count);
        probe.eventTriggerByOwner[owner] = count + 1;
        try
        {
            object atkId = Read(__instance, "attackId");
            // Only pair the atkId with the target it was actually used on: the later block and
            // dodge phases hit other enemies, and a mismatched pair rebuilds a different
            // HitKey, which made the duplicate check fail for the wrong reason.
            bool onMainTarget = probe.phase == Phase.Attack || probe.phase == Phase.AttrA
                || probe.phase == Phase.AttrB || probe.phase == Phase.PositionP2;
            if (owner == 2 && atkId is int && onMainTarget) probe.lastP2AtkId = (int)atkId;
            // V08 needs the real attack ids in the order they were allocated by the game,
            // independent of which phase produced them.
            if (owner == 2 && atkId is int) probe.p2AtkIds.Add((int)atkId);
        }
        catch
        {
        }
    }

    private static void PlayerAttackPostfix(object __instance)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished)
        {
            return;
        }
        probe.attackCalls++;
        if (probe.p2AttackAbility != null && object.ReferenceEquals(__instance, probe.p2AttackAbility))
        {
            probe.attackCallsP2++;
        }
    }

    private void Update()
    {
        if (finished)
        {
            return;
        }
        try
        {
            float budget = 300f + growthMinutes * 60f + 300f;
            if (Time.realtimeSinceStartup - startedAt > budget)
            {
                Fail("the probe finished within its budget");
                return;
            }
            if (cameraBaselineSet)
            {
                Camera live = Camera.main;
                if (live != null)
                {
                    float dev = Vector3.Distance(live.transform.position, cameraBaseline);
                    if (dev > cameraMaxDeviation) cameraMaxDeviation = dev;
                    float sizeDelta = Mathf.Abs(live.orthographicSize - cameraSizeBaseline);
                    if (sizeDelta > cameraMaxSizeDelta) cameraMaxSizeDelta = sizeDelta;
                }
            }
            switch (phase)
            {
                case Phase.LoadScene: LoadScene(); break;
                case Phase.Settle: Settle(); break;
                case Phase.OpenZone: OpenZone(); break;
                case Phase.WaitWave: WaitWave(); break;
                case Phase.PositionP2: PositionP2(); break;
                case Phase.Attack: Attack(); break;
                case Phase.P1Attack: P1Attack(); break;
                case Phase.AttrA: AttrA(); break;
                case Phase.P1LowProbe: P1Probe(false); break;
                case Phase.P1HighProbe: P1Probe(true); break;
                case Phase.LateHit: LateHit(); break;
                case Phase.SplitPlatforms: SplitPlatforms(); break;
                case Phase.DashSplit: DashSplit(); break;
                case Phase.AttrB: AttrB(); break;
                case Phase.BlockTest: BlockTest(); break;
                case Phase.DodgeTest: DodgeTest(); break;
                case Phase.SkillIndep: SkillIndep(); break;
                case Phase.HurtReal: HurtReal(); break;
                case Phase.CameraSettle: CameraSettle(); break;
                case Phase.WipeTest: WipeTest(); break;
                case Phase.FallTest: FallTest(); break;
                case Phase.GrowthLoop: GrowthLoop(); break;
                case Phase.Verify: Verify(); break;
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION " + ex);
            Fail("probe executed without an exception");
        }
    }

    private void LoadScene()
    {
        Type levelManager = FindType("LevelManager");
        Type gate = FindType("IceyCoop.GameBridge.World.SceneLoadGate");
        if (levelManager == null || gate == null)
        {
            if (Time.realtimeSinceStartup - startedAt > 30f)
            {
                Fail("LevelManager and SceneLoadGate are available");
            }
            return;
        }
        string current = Convert.ToString(ReadStaticMember(levelManager, "SceneName"));
        if (string.Equals(current, scene, StringComparison.Ordinal))
        {
            sceneReachedAt = Time.realtimeSinceStartup;
            report.AppendLine("SCENE_REACHED " + current);
            phase = Phase.Settle;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        if (string.IsNullOrEmpty(current) || string.Equals(current, "splash", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (Time.realtimeSinceStartup - startedAt > 60f)
        {
            // A scene name that the story gate refuses would otherwise hang the probe here
            // forever and it would never write its report.
            report.AppendLine("SCENE_TIMEOUT target=" + scene + " current=" + current);
            Fail("the target scene loaded");
            return;
        }
        if (Time.realtimeSinceStartup - sceneRequestAt >= 3f)
        {
            MethodInfo load = gate.GetMethod("LoadAuthorized", BindingFlags.Public | BindingFlags.Static);
            if (load == null)
            {
                Fail("SceneLoadGate.LoadAuthorized is available");
                return;
            }
            load.Invoke(null, new object[] { scene });
            sceneRequestAt = Time.realtimeSinceStartup;
            report.AppendLine("SCENE_REQUEST " + scene + " from=" + current);
        }
    }

    private void Settle()
    {
        if (Time.realtimeSinceStartup - sceneReachedAt < 1.2f)
        {
            return;
        }
        p2Root = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P2Root");
        object binding = EnsureBinding(2);
        if (p2Root == null || binding == null)
        {
            if (Time.realtimeSinceStartup - phaseAt > 45f)
            {
                Fail("P2 root and binding are ready");
            }
            return;
        }
        object abilities = Read(binding, "Abilities");
        p2AttackAbility = Read(abilities, "attack");
        p2JumpAbility = Read(abilities, "jump");
        p2SkillAbility = Read(abilities, "skill");
        object b1 = EnsureBinding(1);
        object a1set = b1 == null ? null : Read(b1, "Abilities");
        p1JumpAbility = Read(a1set, "jump");
        p1SkillAbility = a1set == null ? null : Read(a1set, "skill");
        p1Action = b1 == null ? null : Read(b1, "Action");
        p2Action = binding == null ? null : Read(binding, "Action");
        if (!outcomePatchTried)
        {
            try { InstallOutcomePatch(); } catch { }
        }
        if (!skillPatchTried)
        {
            try { InstallSkillPatch(); } catch { }
        }
        if (!stubPatchTried)
        {
            try { InstallConsumeActionsPatch(); } catch { }
        }
        if (!hurtDeferTried)
        {
            try { InstallHurtDeferPatch(); } catch { }
        }
        if (!commitLogTried)
        {
            try { InstallCommitLogPatch(); } catch { }
        }
        if (!statePatchTried)
        {
            try { InstallStatePatch(); } catch { }
        }
        if (!framePatchTried)
        {
            try { InstallFramePatch(); } catch { }
        }
        report.AppendLine("scene inventory: EnemyAttribute=" + CountLive("EnemyAttribute")
            + " SpiderBossHurt=" + CountLive("SpiderBossHurt")
            + " EatingBossHurt=" + CountLive("EatingBossHurt")
            + " BossFocusAnim=" + CountLive("BossFocusAnim")
            + " BattleCheckPoint=" + CountLive("BattleCheckPoint"));
        if (surveyMode)
        {
            // Open the battle zone first, then dump with real spawned enemies.
            phase = Phase.OpenZone;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        if (fallOnly)
        {
            report.AppendLine("fall-only mode: jumping straight to the fall test");
            phase = Phase.FallTest;
            phaseAt = Time.realtimeSinceStartup;
            fallStage = 0;
            return;
        }
        Check("P2 root, binding and attack ability are ready", p2AttackAbility != null);
        Check("P1 and P2 native jump callbacks are observable", jumpPatchOk && p1JumpAbility != null && p2JumpAbility != null);
        // The probe runs inside a live combat scene for ~1 minute, so the players must not
        // die mid-experiment: raise both HP pools and restore the originals at the end.
        Camera cam = Camera.main;
        if (cam != null)
        {
            cameraBaseline = cam.transform.position;
            cameraSizeBaseline = cam.orthographicSize;
            cameraBaselineSet = true;
            report.AppendLine("camera baseline: pos=" + F(cameraBaseline.x) + "," + F(cameraBaseline.y)
                + " size=" + F(cameraSizeBaseline));
        }
        savedHp1 = BoostPlayerHp(1);
        savedHp2 = BoostPlayerHp(2);
        report.AppendLine("boosted player HP for the run: p1 " + savedHp1 + " -> " + PlayerHp(1)
            + " ; p2 " + savedHp2 + " -> " + PlayerHp(2));
        phase = Phase.OpenZone;
        phaseAt = Time.realtimeSinceStartup;
    }

    private void OpenZone()
    {
        Type bpType = FindType("BattleCheckPoint");
        UnityEngine.Object[] cps = FindLive(bpType);
        if (cps == null || cps.Length == 0)
        {
            if (Time.realtimeSinceStartup - phaseAt > 20f)
            {
                report.AppendLine("the scene has no BattleCheckPoint; this probe needs a combat room");
                Fail("a BattleCheckPoint exists in the scene");
            }
            return;
        }
        Component cp = cps[0] as Component;
        checkpoint = cp;
        Collider2D area = cp.GetComponentInChildren<Collider2D>();
        if (area == null)
        {
            Check("the checkpoint has a trigger collider", false);
            return;
        }
        GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
        if (p1 == null)
        {
            Fail("P1 root is available to walk into the zone");
            return;
        }
        Vector3 center = area.bounds.center;
        p1.transform.position = new Vector3(center.x, center.y, p1.transform.position.z);
        report.AppendLine("walked P1 into the zone trigger at " + center);
        phase = Phase.WaitWave;
        phaseAt = Time.realtimeSinceStartup;
    }

    private void WaitWave()
    {
        // Enemies usually spawn with a short invulnerable window; wait it out before
        // anything is asserted, so the checks below run exactly once.
        if (Time.realtimeSinceStartup - phaseAt < 6f)
        {
            return;
        }
        if (Time.realtimeSinceStartup - phaseAt > 30f)
        {
            Fail("the game spawned a wave after the zone opened");
            return;
        }
        object open = Read(checkpoint, "isOpenBattleZone");
        bool opened = open != null && Convert.ToBoolean(open);
        UnityEngine.Object[] enemies = FindLive(FindType("EnemyAttribute"));
        int count = enemies == null ? 0 : enemies.Length;
        report.AppendLine("zoneOpen=" + opened + " enemies=" + count
            + " currentId=" + Convert.ToString(Read(checkpoint, "currentId")));
        if (surveyMode)
        {
            SurveyDump();
            Finish();
            return;
        }
        Check("the game spawned a wave after the zone opened", opened);
        Check("the wave is live before P2 attacks", count > 0);
        if (CountLive("BossFocusAnim") > 0)
        {
            report.AppendLine("boss room: boss focus anim present, wave size=" + count);
            Check("a boss room holds exactly one boss entity (no duplicated replica)", count == 1);
        }
        phase = Phase.PositionP2;
        phaseAt = Time.realtimeSinceStartup;
    }

    private void PositionP2()
    {
        UnityEngine.Object[] enemies = FindLive(FindType("EnemyAttribute"));
        if (enemies == null || enemies.Length == 0)
        {
            Fail("an enemy is still alive to receive P2's attack");
            return;
        }
        // Keep exactly one enemy: extra enemies keep hitting P2 and interrupt its attacks,
        // which makes the whole measurement flaky.
        Component enemy = null;
        int bestHp = -1;
        for (int i = 0; i < enemies.Length; i++)
        {
            Component candidate = enemies[i] as Component;
            if (candidate == null) continue;
            int candidateHp = EnemyHp(candidate);
            if (candidateHp > bestHp) { bestHp = candidateHp; enemy = candidate; }
        }
        Type hurtType = FindType("EnemyBaseHurt");
        MethodInfo hpMinus = hurtType == null ? null : hurtType.GetMethod("HpMinus", InstanceFlags, null,
            new Type[] { typeof(int) }, null);
        if (hpMinus != null && enemy != null)
        {
            for (int i = 0; i < enemies.Length; i++)
            {
                Component other = enemies[i] as Component;
                if (other == null || object.ReferenceEquals(other, enemy)) continue;
                // Keep Dao alive: its hurt override is the side-step path used by the dodge test.
                string otherName = other.gameObject == null ? "" : other.gameObject.name;
                if (otherName.IndexOf("Dao", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Component hurtComp = other.GetComponent(hurtType);
                if (hurtComp == null) continue;
                if (clearedEnemyId == 0) clearedEnemyId = EntityIdOf(other.gameObject);
                try { hpMinus.Invoke(hurtComp, new object[] { 999999 }); } catch { }
            }
            report.AppendLine("cleared extra enemies so P2 is not interrupted");
        }
        if (enemy == null)
        {
            Fail("an enemy is still alive to receive P2's attack");
            return;
        }
        targetEnemy = enemy;
        enemyPosition = enemy.transform.position;
        enemyHpBefore = EnemyHp(enemy);
        p2HpBefore = enemyHpBefore;
        GameObject p2 = p2Root as GameObject;
        if (p2 == null)
        {
            Fail("P2 root GameObject is available");
            return;
        }
        float side = enemyPosition.x >= p2.transform.position.x ? -1f : 1f;
        p2.transform.position = new Vector3(enemyPosition.x + side * 0.9f, enemyPosition.y, p2.transform.position.z);
        Rigidbody2D body = p2.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.velocity = Vector2.zero;
        }
        report.AppendLine("placed P2 at (" + F(p2.transform.position.x) + "," + F(p2.transform.position.y)
            + ") next to enemy at (" + F(enemyPosition.x) + "," + F(enemyPosition.y) + ") hpBefore=" + enemyHpBefore);
        ledgerSettledBefore = LedgerCount("SettledCount");
        ledgerPendingBefore = LedgerCount("PendingCount");
        report.AppendLine("hit ledger before P2 attack: settled=" + ledgerSettledBefore + " pending=" + ledgerPendingBefore);
        phase = Phase.Attack;
        phaseAt = Time.realtimeSinceStartup;
        attackBursts = 0;
        nextBurstAt = Time.realtimeSinceStartup;
        pendingReleaseAt = 0f;
    }

    private void Attack()
    {
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        int p2Hits = 0;
        eventTriggerByOwner.TryGetValue(2, out p2Hits);
        if (p2Hits > 0)
        {
            report.AppendLine("P2 EventTrigger observed after " + F(elapsed) + "s");
            if (targetEnemy != null) p2HpAfter = EnemyHp(targetEnemy as Component);
            report.AppendLine("target enemy hp after P2 attack: " + p2HpBefore + " -> " + p2HpAfter);
            phase = Phase.P1Attack;
            phaseAt = Time.realtimeSinceStartup;
            p1Pressed = false;
            p1PressedBursts = 0;
            nextBurstAt = Time.realtimeSinceStartup;
            return;
        }
        if (elapsed > 14f)
        {
            Check("P2's own attack reached PlayerAtk.EventTrigger", false);
            phase = Phase.P1Attack;
            phaseAt = Time.realtimeSinceStartup;
            p1Pressed = false;
            p1PressedBursts = 0;
            nextBurstAt = Time.realtimeSinceStartup;
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
        {
            InjectAction(0, false, 0f);
            pendingReleaseAt = 0f;
            return;
        }
        if (now >= nextBurstAt && attackBursts < 10)
        {
            attackBursts++;
            if (targetEnemy != null) PlaceP2NextTo(targetEnemy as Component);
            float towards = enemyPosition.x >= ((GameObject)p2Root).transform.position.x ? 1f : -1f;
            InjectAction(0, true, towards);
            // InputAdapter goes neutral after 150ms without a frame, so the release must
            // arrive well inside that window or the router resets and the tap is lost.
            pendingReleaseAt = now + 0.08f;
            nextBurstAt = now + 1.1f;
            report.AppendLine("injected P2 attack burst " + attackBursts + " towards " + F(towards));
        }
    }
    /// <summary>
    /// V14 proper: 闂備焦鎮堕崕顖炲垂缂佹ê顕?means the party dies, not that the enemies are cleared. Kill both
    /// players, let the co-op wipe handling restore them, then walk back into the battle zone
    /// and see whether the encounter arms a fresh wave.
    /// </summary>
    private void WipeTest()
    {
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        if (wipeStage == 0)
        {
            wipeStage = 1;
            wipeWaveBefore = checkpoint == null ? -1 : Convert.ToInt32(Read(checkpoint, "currentId"));
            report.AppendLine("wipe test: killing both players (waveId=" + wipeWaveBefore + ")");
            Type hurtType = FindType("PlayerHurtAbility");
            MethodInfo hpMinus = hurtType == null ? null : hurtType.GetMethod("HpMinus", InstanceFlags, null,
                new Type[] { typeof(int) }, null);
            foreach (byte player in new byte[] { 1, 2 })
            {
                try
                {
                    object binding = EnsureBinding(player);
                    object hurt = binding == null ? null : Read(binding, "HurtAbility");
                    if (hurt != null && hpMinus != null) hpMinus.Invoke(hurt, new object[] { 999999 });
                }
                catch (Exception ex)
                {
                    report.AppendLine("wipe test: killing P" + player + " threw " + ex.Message);
                }
            }
            wipeStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (wipeStage == 1)
        {
            int h1 = PlayerHp(1);
            int h2 = PlayerHp(2);
            bool alive = h1 > 0 && h2 > 0;
            if (alive)
            {
                report.AppendLine("wipe test: both players back alive after " + F(elapsed) + "s (hp "
                    + h1 + "/" + h2 + ")");
                Check("both players were restored after a party wipe", true);
            }
            if (alive || elapsed > 40f)
            {
                if (!alive)
                {
                    report.AppendLine("wipe test: players not restored within 40s (hp " + h1 + "/" + h2 + ")");
                    Check("both players were restored after a party wipe", false);
                }
                wipeSceneAfter = SceneNameOf();
                report.AppendLine("wipe test: room after the wipe: scene " + wipeSceneBefore
                    + " -> " + wipeSceneAfter);
                wipeStage = 2;
                wipeStageAt = Time.realtimeSinceStartup;
            }
            return;
        }
        if (wipeStage == 2)
        {
            // The wipe restore reloads the room, so the cached checkpoint is a destroyed object
            // by now, and the reloaded room needs a moment before it exposes a fresh one.
            Component cp2 = null;
            try { cp2 = RefreshCheckpoint() as Component; }
            catch { cp2 = null; }
            if (cp2 == null)
            {
                float waited = Time.realtimeSinceStartup - wipeStageAt;
                if (!wipeSwitchDone && waited > 12f)
                {
                    // The wipe restore drops the party back on its saved checkpoint, which can be
                    // a story room with no battle zone at all. Load a combat room so the
                    // "re-enter the zone after a wipe" half can still be measured.
                    wipeSwitchDone = true;
                    report.AppendLine("wipe test: the restored room (" + SceneNameOf()
                        + ") has no battle zone; loading " + wipeSwitchTo + " to re-enter a room");
                    try { LoadSceneViaGate(wipeSwitchTo); }
                    catch (Exception ex) { report.AppendLine("wipe test: scene switch threw " + ex.Message); }
                    wipeStage = 8;
                    wipeStageAt = Time.realtimeSinceStartup;
                    return;
                }
                if (waited < 30f) return;
                wipeZoneFound = false;
                report.AppendLine("wipe test: the room after the wipe exposes no BattleCheckPoint after 30s (scene="
                    + SceneNameOf() + ")");
                report.AppendLine("OBSERVE a party wipe left a battle zone to re-enter=False");
                wipeStage = 7;
                EndWipeRun();
                return;
            }
            checkpoint = cp2;
            wipeZoneFound = true;
            Collider2D area = cp2.GetComponentInChildren<Collider2D>();
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (area != null && p1 != null)
            {
                Vector3 c0 = area.bounds.center;
                p1.transform.position = new Vector3(c0.x, c0.y, p1.transform.position.z);
            }
            report.AppendLine("wipe test: walked P1 back into the battle zone at "
                + (area == null ? "?" : area.bounds.center.ToString()));
            wipeStage = 3;
            wipeStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (wipeStage == 3)
        {
            int enemies = CountLive("EnemyAttribute");
            if (enemies > 0)
            {
                FinishWipe(enemies, "re-entry");
                return;
            }
            if (Time.realtimeSinceStartup - wipeStageAt > 12f)
            {
                // A zone that is still marked open will not fire again: step well out of the
                // trigger first, exactly like the cleared-room respawn test does.
                StepP1OutOfZone();
                report.AppendLine("wipe test: no wave in the zone after 12s; stepped P1 out of the trigger");
                wipeStage = 4;
                wipeStageAt = Time.realtimeSinceStartup;
            }
            return;
        }
        if (wipeStage == 4)
        {
            if (Time.realtimeSinceStartup - wipeStageAt < 2.5f) return;
            if (!StepP1IntoZone())
            {
                report.AppendLine("wipe test: the trigger centre is gone; cannot re-enter the zone");
                FinishWipe(CountLive("EnemyAttribute"), "re-entry (no trigger)");
                return;
            }
            report.AppendLine("wipe test: walked P1 into the trigger again");
            wipeStage = 5;
            wipeStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (wipeStage == 5)
        {
            int enemies = CountLive("EnemyAttribute");
            if (enemies > 0 || Time.realtimeSinceStartup - wipeStageAt > 14f)
            {
                FinishWipe(enemies, "second re-entry");
                return;
            }
            return;
        }
        if (wipeStage == 8)
        {
            string current = SceneNameOf();
            if (string.Equals(current, wipeSwitchTo, StringComparison.Ordinal))
            {
                report.AppendLine("wipe test: loaded the combat room " + current + " after the wipe");
                wipeStage = 2;
                wipeStageAt = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - wipeStageAt > 45f)
            {
                report.AppendLine("wipe test: the scene switch after the wipe did not land (scene="
                    + current + ")");
                report.AppendLine("OBSERVE a party wipe left a battle zone to re-enter=False");
                wipeStage = 7;
                EndWipeRun();
            }
            return;
        }
    }

    private void FinishWipe(int enemies, string how)
    {
        wipeEnemiesAfter = enemies;
        wipeWaveAfter = checkpoint == null ? "?" : Convert.ToString(Read(checkpoint, "currentId"));
        report.AppendLine("wipe test: after " + how + " enemies=" + wipeEnemiesAfter
            + " waveId=" + wipeWaveAfter + " (before wipe waveId=" + wipeWaveBefore
            + ", scene " + wipeSceneBefore + " -> " + wipeSceneAfter + ")");
        report.AppendLine("OBSERVE a party wipe re-armed the encounter=" + (wipeEnemiesAfter > 0)
            + " (new generation=" + (!string.IsNullOrEmpty(wipeWaveAfter)
                && wipeWaveAfter != Convert.ToString(wipeWaveBefore)) + ")");
        Check("re-entering the battle zone after a party wipe re-arms the encounter", wipeEnemiesAfter > 0);
        wipeStage = 7;
        EndWipeRun();
    }

    private void EndWipeRun()
    {
        if (growthMinutes > 0f)
        {
            growthStartedAt = Time.realtimeSinceStartup;
            growthNextSampleAt = growthStartedAt;
            phase = Phase.GrowthLoop;
        }
        else
        {
            Finish();
        }
    }

    private void LoadSceneViaGate(string target)
    {
        Type gate = FindType("IceyCoop.GameBridge.World.SceneLoadGate");
        MethodInfo load = gate == null ? null : gate.GetMethod("LoadAuthorized", BindingFlags.Public | BindingFlags.Static);
        if (load == null) throw new MissingMethodException("SceneLoadGate.LoadAuthorized");
        load.Invoke(null, new object[] { target });
    }

    private bool StepP1IntoZone()
    {
        try
        {
            Component cp = checkpoint as Component;
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (cp == null || p1 == null) return false;
            Collider2D area = cp.GetComponentInChildren<Collider2D>();
            if (area == null) return false;
            Vector3 c0 = area.bounds.center;
            p1.transform.position = new Vector3(c0.x, c0.y, p1.transform.position.z);
            Rigidbody2D body = p1.GetComponent<Rigidbody2D>();
            if (body != null) body.velocity = Vector2.zero;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void StepP1OutOfZone()
    {
        try
        {
            Component cp = checkpoint as Component;
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (cp == null || p1 == null) return;
            Collider2D area = cp.GetComponentInChildren<Collider2D>();
            if (area == null) return;
            Vector3 c0 = area.bounds.center;
            p1.transform.position = new Vector3(c0.x + 20f, c0.y, p1.transform.position.z);
        }
        catch
        {
        }
    }

    /// <summary>
    /// V14: after a room is cleared, re-entering its battle zone should be able to start a new
    /// wave again (a fresh generation). Observation-only: a room that the game keeps cleared is
    /// a legitimate game behaviour, so the probe reports what happened instead of failing.
    /// </summary>
    private void RespawnTest()
    {
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
        if (respawnStage == 0)
        {
            respawnStage = 1;
            Type hurtType = FindType("EnemyBaseHurt");
            MethodInfo hpMinus = hurtType == null ? null : hurtType.GetMethod("HpMinus", InstanceFlags, null,
                new Type[] { typeof(int) }, null);
            UnityEngine.Object[] enemies = FindLive(FindType("EnemyAttribute"));
            respawnBefore = enemies == null ? 0 : enemies.Length;
            if (hpMinus != null && enemies != null)
            {
                for (int i = 0; i < enemies.Length; i++)
                {
                    Component enemy = enemies[i] as Component;
                    if (enemy == null) continue;
                    Component hurt = enemy.GetComponent(hurtType);
                    if (hurt == null) continue;
                    try { hpMinus.Invoke(hurt, new object[] { 999999 }); } catch { }
                }
            }
            report.AppendLine("respawn test: cleared " + respawnBefore + " enemies, waveId="
                + Convert.ToString(Read(checkpoint, "currentId")));
            return;
        }
        if (respawnStage == 1)
        {
            // Keep killing survivors: the room only records "cleared" once nothing is left,
            // and a room that still holds an enemy just re-arms the same wave id.
            Type hurtT2 = FindType("EnemyBaseHurt");
            MethodInfo minus2 = hurtT2 == null ? null : hurtT2.GetMethod("HpMinus", InstanceFlags, null,
                new Type[] { typeof(int) }, null);
            UnityEngine.Object[] still = FindLive(FindType("EnemyAttribute"));
            if (still != null && minus2 != null)
            {
                for (int i = 0; i < still.Length; i++)
                {
                    Component e2 = still[i] as Component;
                    if (e2 == null) continue;
                    Component h2 = e2.GetComponent(hurtT2);
                    if (h2 == null) continue;
                    try { minus2.Invoke(h2, new object[] { 999999 }); } catch { }
                }
            }
            int leftNow = CountLive("EnemyAttribute");
            if (leftNow > 0 && elapsed < 25f) return;
            int left = leftNow;
            report.AppendLine("respawn test: enemies after clear = " + left
                + ", battleOver=" + Convert.ToString(Read(checkpoint, "battleOver")));
            if (p1 != null && checkpoint != null)
            {
                Collider2D area = ((Component)checkpoint).GetComponentInChildren<Collider2D>();
                if (area != null)
                {
                    Vector3 c0 = area.bounds.center;
                    // step well out of the zone first so the trigger can fire again on re-entry
                    p1.transform.position = new Vector3(c0.x + 20f, c0.y, p1.transform.position.z);
                }
            }
            respawnStage = 2;
            respawnStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (respawnStage == 2)
        {
            if (Time.realtimeSinceStartup - respawnStageAt < 2.5f) return;
            if (p1 != null && checkpoint != null)
            {
                Collider2D area = ((Component)checkpoint).GetComponentInChildren<Collider2D>();
                if (area != null)
                {
                    Vector3 c0 = area.bounds.center;
                    p1.transform.position = new Vector3(c0.x, c0.y, p1.transform.position.z);
                }
            }
            report.AppendLine("respawn test: walked P1 back into the zone");
            respawnStage = 3;
            respawnStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (respawnStage == 3)
        {
            if (Time.realtimeSinceStartup - respawnStageAt < 8f) return;
            respawnAfter = CountLive("EnemyAttribute");
            string waveAfter = Convert.ToString(Read(checkpoint, "currentId"));
            report.AppendLine("respawn test: enemies after re-entry = " + respawnAfter
                + " (before clear " + respawnBefore + "), waveId=" + waveAfter);
            report.AppendLine("OBSERVE a cleared room re-spawned a wave=" + (respawnAfter > 0));
            respawnStage = 4;
            phase = Phase.Verify;
            return;
        }
    }

    private void Verify()
    {
        int p2Hits = 0;
        eventTriggerByOwner.TryGetValue(2, out p2Hits);
        int p1Hits = 0;
        eventTriggerByOwner.TryGetValue(1, out p1Hits);
        int unknownHits = 0;
        foreach (KeyValuePair<byte, int> pair in eventTriggerByOwner)
        {
            if (pair.Key != 1 && pair.Key != 2)
            {
                unknownHits += pair.Value;
            }
        }
        report.AppendLine("EventTrigger by owner: P1=" + p1Hits + " P2=" + p2Hits + " unknown=" + unknownHits);
        report.AppendLine("PlayerAttackAbility.PlayerAttack calls: total=" + attackCalls + " P2=" + attackCallsP2);
        if (targetEnemy != null)
        {
            Component enemy = targetEnemy as Component;
            enemyHpAfter = EnemyHp(enemy);
            report.AppendLine("target enemy hp " + enemyHpBefore + " -> " + enemyHpAfter);
        }
        Check("P2's own attack reached PlayerAtk.EventTrigger", p2Hits > 0);
        Check("the P2 attack was attributed to owner P2, not P1", p2Hits > 0 && p2Hits >= p1Hits);
        Check("P2's PlayerAttackAbility was driven by the production dispatch", attackCallsP2 > 0);
        Check("P1 attack was attributed to owner P1, not P2", p1Hits > 0);
        report.AppendLine("attribution follows the attacker: P2=" + p2Hits + " P1=" + p1Hits);
        int attrLowDelta = attrLowHpBefore >= 0 && attrLowHpAfter >= 0 ? attrLowHpBefore - attrLowHpAfter : -1;
        int attrHighDelta = attrHighHpBefore >= 0 && attrHighHpAfter >= 0 ? attrHighHpBefore - attrHighHpAfter : -1;
        report.AppendLine("attr result: Attack=0 -> " + attrLowDelta + " ; Attack=3 -> " + attrHighDelta);
        int p1LowDelta = p1LowHpBefore >= 0 && p1LowHpAfter >= 0 ? p1LowHpBefore - p1LowHpAfter : -1;
        int p1HighDelta = p1HighHpBefore >= 0 && p1HighHpAfter >= 0 ? p1HighHpBefore - p1HighHpAfter : -1;
        report.AppendLine("P1 damage while P2 neutered=" + p1LowDelta + " ; while P2 buffed=" + p1HighDelta);
        report.AppendLine("P1 owned settlement samples: low=" + p1LowOwnedSample + " high=" + p1HighOwnedSample);
        Check("P1 low window recorded a committed P1 hit on the fixture target", p1LowOwnedSample);
        Check("P1 high window recorded a committed P1 hit on the fixture target", p1HighOwnedSample);
        // Tolerance re-derived from the measured same-condition spread: seven runs of this probe
        // gave P1 damage 50/52/53/55/57/57/57 against 54/54/55/55/59/64 in the two windows, i.e.
        // a +/-28% swing with no buff at all, so the old 25% window sat inside the noise. A real
        // leak would double P1 damage the way P2 own damage doubles (50 -> 117).
        int leakTolerance = Math.Max(20, p1LowDelta * 45 / 100);
        bool leakMeasured = p1LowDelta > 0 && p1HighDelta > 0;
        report.AppendLine("OBSERVE leak comparison measured=" + leakMeasured);
        Check("P1 damage was measured in both windows", leakMeasured);
        if (leakMeasured)
        {
            Check("P2 buff did not leak into P1 settlement", Math.Abs(p1HighDelta - p1LowDelta) <= leakTolerance);
        }
        Check("P2 attack with Enhancement.Attack=0 dealt damage", attrLowDelta > 0);
        Check("P2 attack with Enhancement.Attack=3 dealt damage", attrHighDelta > 0);
        Check("damage changes with the attacker Attribute.Attack", attrLowDelta > 0 && attrHighDelta > 0 && attrLowDelta != attrHighDelta);
        report.AppendLine("hurt routing: p1 " + p1HurtBefore + "->" + p1HurtAfter + " ; p2 " + p2HurtBefore + "->" + p2HurtAfter);
        // V16: what an enemy projectile locks onto, and which action states the players went
        // through while the boss was attacking them.
        report.AppendLine("V16 action states: p1=[" + string.Join(",", new List<string>(p1ActionStates).ToArray())
            + "] p2=[" + string.Join(",", new List<string>(p2ActionStates).ToArray()) + "]");
        report.AppendLine("V16 enemy projectiles: samples=" + bulletSamples + " lockedP1=" + bulletLockedP1
            + " lockedP2=" + bulletLockedP2 + " unknown=" + bulletLockedUnknown);
        if (bulletSamples > 0)
        {
            bool lockedOneSide = bulletLockedP1 + bulletLockedP2 > 0 && ((bulletLockedP1 > 0) != (bulletLockedP2 > 0));
            report.AppendLine("OBSERVE an enemy projectile locked onto exactly one player=" + lockedOneSide
                + " (locked p1=" + bulletLockedP1 + " p2=" + bulletLockedP2 + " samples=" + bulletSamples + ")");
            if (bulletLockedP1 + bulletLockedP2 > 0) Check("an enemy projectile locked onto a specific player", lockedOneSide);
            else report.AppendLine("OBSERVE the projectile seen here was not locked to either player root, so the lock-on half of V16 stays unmeasured");
        }
        else
        {
            report.AppendLine("OBSERVE no enemy projectile appeared in this room, so the lock-on half of"
                + " V16 stays unmeasured here");
        }
        bool downState = false;
        foreach (string state in p1ActionStates) if (LooksLikeDownState(state)) downState = true;
        foreach (string state in p2ActionStates) if (LooksLikeDownState(state)) downState = true;
        if (downState)
        {
            Check("a player was knocked down and went through the get-up state", true);
        }
        else
        {
            report.AppendLine("OBSERVE the boss never knocked a player down in this run, so the knockdown half of V16 stays unmeasured");
        }
        int settledAfter = LedgerCount("SettledCount");
        int pendingAfter = LedgerCount("PendingCount");
        report.AppendLine("hit ledger after the run: settled=" + settledAfter + " (delta "
            + (settledAfter - ledgerSettledBefore) + ") pending=" + pendingAfter);
        Check("real hits were committed into the hit ledger",
            ledgerSettledBefore >= 0 && settledAfter > ledgerSettledBefore);
        Check("no hit settlement leaked as pending", pendingAfter == 0);
        DuplicateSettlementCheck();
        RewardDedupCheck();
        ExecutionLockCheck();
        ExecutionCandidateGuardCheck();
        CameraRestoreCheck();
        int oDamage, oBlocked, oDodged, oRejected;
        outcomes.TryGetValue(1, out oDamage);
        outcomes.TryGetValue(2, out oBlocked);
        outcomes.TryGetValue(3, out oDodged);
        outcomes.TryGetValue(4, out oRejected);
        report.AppendLine("block path observed this run: " + blockObserved);
        report.AppendLine("dodge path observed this run: " + dodgeObserved);
        report.AppendLine("hit outcomes resolved natively: Damage=" + oDamage + " Blocked=" + oBlocked
            + " Dodged=" + oDodged + " Rejected=" + oRejected);
        bool hpFell = p2HpBefore >= 0 && p2HpAfter >= 0 && p2HpAfter < p2HpBefore;
        Check("a real hit resolved to outcome Damage", oDamage > 0);
        // Several hits land inside one phase window, so a Blocked capture is not evidence that
        // the HP loss came from a block. Keep the two facts separate instead of asserting a
        // false implication: the damaging path must be recorded, and if the dedicated block
        // test observed a native block, the Blocked outcome must be there too.
        Check("a damaging hit resolved to outcome Damage while HP fell", !hpFell || oDamage > 0);
        Check("an observed native block is recorded as the Blocked outcome", !blockObserved || oBlocked > 0);
        report.AppendLine("NOTE: enemy HP change is informational; damage magnitude / attribute choice needs a "
            + "per-owner attack-power comparison that this probe does not set up yet.");
        if (fallTestEnabled)
        {
            report.AppendLine("fall test armed (runs after all other checks)");
            phase = Phase.FallTest;
            phaseAt = Time.realtimeSinceStartup;
            fallStage = 0;
            return;
        }
        if (wipeTestEnabled)
        {
            // Runs last: the wipe restore reloads the room and resets the world epoch, which
            // would invalidate every check above.
            report.AppendLine("wipe test armed (runs after all other checks)");
            phase = Phase.WipeTest;
            phaseAt = Time.realtimeSinceStartup;
            wipeStage = 0;
            return;
        }
        if (growthMinutes > 0f)
        {
            report.AppendLine("growth loop armed for " + F(growthMinutes) + " minutes");
            growthStartedAt = Time.realtimeSinceStartup;
            growthNextSampleAt = growthStartedAt;
            phase = Phase.GrowthLoop;
            return;
        }
        Finish();
    }

    /// <summary>
    /// V32 long-session half: keep the session alive and sample memory / live enemies / replicas
    /// every 30 seconds, so growth can be read off first-vs-last instead of guessed.
    /// </summary>
    /// <summary>
    /// V23: falling out of the room. The probe looks for a column of empty space beside the room,
    /// drops P1 into it, and then watches what the game does with the falling player - that is the
    /// "坠崖" half of V23, which no other probe touched.
    /// </summary>
    private void FallTest()
    {
        if (fallStage == 0)
        {
            fallStage = 1;
            fallStartY = PlayerY(1);
            fallHpBefore = PlayerHp(1);
            float pitX = float.NaN;
            try
            {
                Vector3 origin = Vector3.zero;
                GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
                if (p1 == null)
                {
                    report.AppendLine("fall test skipped: no P1 root");
                    fallStage = 9;
                    EndFallTest();
                    return;
                }
                origin = p1.transform.position;
                for (float x = origin.x - 70f; x <= origin.x + 70f; x += 1f)
                {
                    RaycastHit2D hit = Physics2D.Raycast(new Vector2(x, origin.y + 8f), Vector2.down, 300f);
                    if (hit.collider != null && hit.point.y >= origin.y - 8f) continue;
                    pitX = x;
                    break;
                }
                fallFoundPit = !float.IsNaN(pitX);
                if (fallFoundPit)
                {
                    p1.transform.position = new Vector3(pitX, origin.y + 0.6f, origin.z);
                    Rigidbody2D body = p1.GetComponent<Rigidbody2D>();
                    if (body != null) body.velocity = Vector2.zero;
                    report.AppendLine("fall test: dropped P1 into the empty column at x=" + F(pitX)
                        + " (start y=" + F(origin.y) + ", hp=" + fallHpBefore + ")");
                }
                else
                {
                    report.AppendLine("fall test: no empty column next to the room, so there is no pit to fall into");
                    report.AppendLine("OBSERVE the room has a pit to fall into=False");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("fall test: scanning for a pit failed " + ex.Message);
            }
            fallHpMin = fallHpBefore;
            fallStageAt = Time.realtimeSinceStartup;
            if (!fallFoundPit)
            {
                fallStage = 9;
                EndFallTest();
            }
            return;
        }
        if (fallStage == 1)
        {
            float y = PlayerY(1);
            int hp = PlayerHp(1);
            if (hp >= 0 && (fallHpMin < 0 || hp < fallHpMin)) fallHpMin = hp;
            float elapsed = Time.realtimeSinceStartup - fallStageAt;
            bool fell = y < fallStartY - 3f;
            bool hurt = fallHpBefore >= 0 && fallHpMin >= 0 && fallHpMin < fallHpBefore;
            if (fell && (hurt || elapsed > 1.5f))
            {
                report.AppendLine("fall test: P1 fell from y=" + F(fallStartY) + " to y=" + F(y)
                    + " after " + F(elapsed) + "s, hp " + fallHpBefore + " -> " + fallHpMin);
                fallStage = 2;
                fallStageAt = Time.realtimeSinceStartup;
                return;
            }
            if (elapsed > 8f)
            {
                report.AppendLine("fall test: P1 did not fall (y=" + F(y) + ", hp " + fallHpBefore
                    + " -> " + fallHpMin + ")");
                report.AppendLine("OBSERVE the dropped player actually fell=False");
                fallStage = 9;
                EndFallTest();
            }
            return;
        }
        if (fallStage == 2)
        {
            // Give the game a moment to apply fall damage / respawn / party restore.
            float elapsed = Time.realtimeSinceStartup - fallStageAt;
            int hp = PlayerHp(1);
            if (hp >= 0 && (fallHpMin < 0 || hp < fallHpMin)) fallHpMin = hp;
            if (elapsed < 6f) return;
            string sceneNow = SceneNameOf();
            report.AppendLine("fall test: 6s after the fall y=" + F(PlayerY(1)) + " hp=" + hp
                + " (min " + fallHpMin + ") scene=" + sceneNow);
            report.AppendLine("OBSERVE the fall cost HP=" + (fallHpBefore >= 0 && fallHpMin < fallHpBefore));
            report.AppendLine("OBSERVE the session survived the fall=" + (!finished && !string.IsNullOrEmpty(sceneNow)
                && sceneNow != "?"));
            Check("the session survived a player falling out of the room",
                !finished && !string.IsNullOrEmpty(sceneNow) && sceneNow != "?");
            fallStage = 9;
            EndFallTest();
            return;
        }
    }

    private void EndFallTest()
    {
        if (wipeTestEnabled)
        {
            report.AppendLine("wipe test armed (runs after the fall test)");
            phase = Phase.WipeTest;
            phaseAt = Time.realtimeSinceStartup;
            wipeStage = 0;
            return;
        }
        if (growthMinutes > 0f)
        {
            growthStartedAt = Time.realtimeSinceStartup;
            growthNextSampleAt = growthStartedAt;
            phase = Phase.GrowthLoop;
            return;
        }
        Finish();
    }

    private void GrowthLoop()
    {
        float now = Time.realtimeSinceStartup;
        float elapsed = now - growthStartedAt;
        if (now >= growthNextSampleAt)
        {
            growthNextSampleAt = now + 30f;
            growthSamples++;
            long mem = GC.GetTotalMemory(false);
            int enemies = CountLive("EnemyAttribute");
            int clones = CountClones();
            BoostPlayerHp(1);
            BoostPlayerHp(2);
            report.AppendLine("[growth] t=" + (int)elapsed + "s mem=" + mem + " enemies=" + enemies
                + " clones=" + clones);
            if (growthFirstMem < 0)
            {
                growthFirstMem = mem;
                growthFirstEnemies = enemies;
                growthFirstClones = clones;
            }
            growthLastMem = mem;
            growthLastEnemies = enemies;
            growthLastClones = clones;
        }
        if (!growthSwitched && !string.IsNullOrEmpty(growthSwitchTo) && growthSwitchAt > 0f
            && elapsed >= growthSwitchAt)
        {
            growthSwitched = true;
            growthSceneSwitches++;
            growthOpenZoneAt = elapsed + 10f;
            report.AppendLine("[growth] scene switch at t=" + (int)elapsed + "s -> " + growthSwitchTo);
            try
            {
                Type gate = FindType("IceyCoop.GameBridge.World.SceneLoadGate");
                MethodInfo load = gate == null ? null : gate.GetMethod("LoadAuthorized", BindingFlags.Public | BindingFlags.Static);
                if (load != null) load.Invoke(null, new object[] { growthSwitchTo });
            }
            catch (Exception ex)
            {
                report.AppendLine("[growth] scene switch threw: " + ex.Message);
            }
        }
        // After a scene switch the new room starts closed; walk P1 into its checkpoint so the
        // long session keeps real combat going instead of idling in an empty room.
        if (growthOpenZoneAt > 0f && elapsed >= growthOpenZoneAt)
        {
            growthOpenZoneAt = -1f;
            Type bp = FindType("BattleCheckPoint");
            UnityEngine.Object[] cps = FindLive(bp);
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (cps != null && cps.Length > 0 && p1 != null)
            {
                Component cp = cps[0] as Component;
                Collider2D area = cp.GetComponentInChildren<Collider2D>();
                if (area != null)
                {
                    Vector3 center = area.bounds.center;
                    p1.transform.position = new Vector3(center.x, center.y, p1.transform.position.z);
                    growthZonesOpened++;
                    report.AppendLine("[growth] opened the new room zone at t=" + (int)elapsed + "s");
                }
            }
            else
            {
                report.AppendLine("[growth] no checkpoint in the new scene at t=" + (int)elapsed + "s");
            }
        }
        if (elapsed < growthMinutes * 60f)
        {
            return;
        }
        Finish();
    }

    private object RefreshCheckpoint()
    {
        try
        {
            Type bp = FindType("BattleCheckPoint");
            UnityEngine.Object[] cps = FindLive(bp);
            if (cps != null && cps.Length > 0) return cps[0];
        }
        catch
        {
        }
        return null;
    }

    private int CountClones()
    {
        try
        {
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>();
            int clones = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].gameObject.name.EndsWith("(Clone)", StringComparison.Ordinal)) clones++;
            }
            return clones;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Same enemy, but attacked by P1 through the vanilla input path: the probe only
    /// sets the native Core.Input.Game.Atk physical flag, exactly like the client probe
    /// does, so P1's own PlayerInput.Update drives PlayerAttack. If that hit is then
    /// attributed to P1 while P2's was attributed to P2, the routing follows the
    /// attacker instead of being pinned to one owner.
    /// </summary>
    /// <summary>
    /// Real enemy -> player hurt routing. Both players are parked next to a live enemy and
    /// the probe only waits: any damage must come from the enemy's own attack. Each player's
    /// own HP must drop, which is what "the event routes to the real victim" means - a
    /// mis-routed hit would change the other player's HP instead.
    /// </summary>
    /// <summary>
    /// Blocked-outcome path. CalculateMonsterDefence only fires for an enemy whose
    /// monsterDefence is above zero, and only once the accumulated currentDefence reaches
    /// it, so a stock C2L1S7 enemy never blocks. The probe lowers monsterDefence to 1 on the
    /// target and keeps hitting it until the native roll blocks once; the HP must stay put
    /// on that hit and the captured outcome must be Blocked.
    /// </summary>
    /// <summary>
    /// V03/V05: while P2's attack attributes are raised (0 -> 3), P1 must still settle with
    /// P1's own attributes. The probe hits the same enemy with P1 once before the buff and
    /// once after, and compares the two damage numbers.
    /// </summary>
    private void P1Probe(bool high)
    {
        if (!p1ProbeInit)
        {
            p1ProbeInit = true;
            if (high)
            {
                p1HighOwnedSample = false;
                p1HighHpBefore = -1;
                p1HighHpAfter = -1;
            }
            else
            {
                p1LowOwnedSample = false;
                p1LowHpBefore = -1;
                p1LowHpAfter = -1;
            }
            Component target = attrTarget;
            if (target == null)
            {
                report.AppendLine("P1 probe skipped: no target");
                FinishP1Probe(high);
                return;
            }
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            Vector3 tp = ((Component)target).transform.position;
            if (p1 != null)
            {
                p1.transform.position = new Vector3(tp.x - 0.15f, tp.y, p1.transform.position.z);
                Rigidbody2D body = p1.GetComponent<Rigidbody2D>();
                if (body != null) body.velocity = Vector2.zero;
            }
            int hp = EnemyHp(target);
            report.AppendLine("P1 probe (" + (high ? "P2 buffed" : "P2 neutered")
                + "): waiting for a committed P1-owned hit on attrTarget=" + target.gameObject.name + " fixture hp=" + hp);
            p1ProbeHeld = false;
            p1ProbeNextAt = Time.realtimeSinceStartup;
            return;
        }
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        Component tgt = attrTarget;
        int cur = EnemyHp(tgt);
        bool ownedSample = high ? p1HighOwnedSample : p1LowOwnedSample;
        if (ownedSample)
        {
            int before = high ? p1HighHpBefore : p1LowHpBefore;
            int after = high ? p1HighHpAfter : p1LowHpAfter;
            report.AppendLine("P1 probe (" + (high ? "P2 buffed" : "P2 neutered")
                + ") committed own-hit damage = " + (before - after));
            FinishP1Probe(high);
            return;
        }
        if (elapsed > 12f)
        {
            report.AppendLine("P1 probe (" + (high ? "P2 buffed" : "P2 neutered")
                + ") had no committed P1-owned hit on attrTarget within 12s (currentHP=" + cur + ")");
            FinishP1Probe(high);
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (p1ProbeHeld && now - p1ProbeHeldAt >= 0.10f)
        {
            SetRawButton(false);
            p1ProbeHeld = false;
            p1ProbeNextAt = now + 0.8f;
        }
        else if (!p1ProbeHeld && now >= p1ProbeNextAt)
        {
            SetRawButton(true);
            p1ProbeHeld = true;
            p1ProbeHeldAt = now;
        }
    }

    private void FinishP1Probe(bool high)
    {
        SetRawButton(false);
        if (high)
        {
            RestoreAttack();
            phase = Phase.LateHit;
        }
        else
        {
            phase = Phase.AttrB;
        }
        phaseAt = Time.realtimeSinceStartup;
    }

    /// <summary>
    /// V08: attack X hits before attack Y, but X's settlement lands after Y's.
    /// The game allocates one fresh native atkId per hit and the vanilla EnemyBaseHurt keeps a
    /// numeric high-water mark (hurtId &gt;= atkId drops the hit), so a late-but-valid hit used
    /// to be thrown away. The production ledger keys hits by identity (epoch/attacker/attack
    /// seq/target/ordinal), so both keys must settle.
    ///
    /// The probe forces the ordering instead of hoping for it: the FIRST real hit is held back
    /// before the production settlement runs, the second real hit settles normally, and then
    /// the first hit is replayed straight through the native EnemyBaseHurt.NormalHurt. Both
    /// atkIds come from P2's own attacks, nothing is fabricated.
    /// </summary>
    private void LateHit()
    {
        if (lateStage == 0)
        {
            lateStage = 1;
            Component target = attrTarget != null ? attrTarget : PickToughestEnemy();
            if (target == null || !vetoHookOk)
            {
                report.AppendLine("late hit check skipped: target=" + (target != null)
                    + " commitVetoHook=" + vetoHookOk);
                lateStage = 9;
                phase = Phase.SplitPlatforms;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            lateTarget = target;
            lateTargetId = EntityIdOf(target.gameObject);
            lateSettledBefore = LedgerCount("SettledCount");
            lateAtkStart = p2AtkIds.Count;
            deferredCaptured = false;
            deferredReplaying = false;
            deferredHolds = 0;
            deferredKey = null;
            deferArmed = true;
            attackBursts = 0;
            pendingReleaseAt = 0f;
            nextBurstAt = Time.realtimeSinceStartup;
            PumpIntervalSeconds = 1.0f;
            report.AppendLine("late hit check: target id=" + lateTargetId + " ledger settled="
                + lateSettledBefore + " ; holding the next real hit back until one more settles");
            return;
        }
        if (lateStage == 1)
        {
            float elapsed = Time.realtimeSinceStartup - phaseAt;
            int settledNow = LedgerCount("SettledCount");
            if (deferredCaptured && settledNow > lateSettledBefore)
            {
                if (p2AtkIds.Count > lateAtkStart + 1) lateSecondAtkId = p2AtkIds[lateAtkStart + 1];
                else if (p2AtkIds.Count > 0) lateSecondAtkId = p2AtkIds[p2AtkIds.Count - 1];
                ReplayDeferredHit();
                return;
            }
            if (elapsed > 22f)
            {
                report.AppendLine("late hit check: could not stage two hits (held=" + deferredCaptured
                    + " settled " + lateSettledBefore + " -> " + settledNow + ")");
                deferArmed = false;
                lateStage = 9;
                phase = Phase.SplitPlatforms;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            PumpP2Attack();
            return;
        }
    }

    /// <summary>
    /// Settles the held key after a later hit already settled. By now the enemy's native hurtId
    /// has moved past this atkId, so the vanilla high-water mark would drop the hit; only the
    /// identity-keyed ledger can still accept it.
    /// </summary>
    private void ReplayDeferredHit()
    {
        lateStage = 2;
        deferArmed = false;
        object keyFirst = deferredKey;
        if (keyFirst == null)
        {
            TryBuildHitKey(deferredAtkId, lateTargetId, out keyFirst);
        }
        object keySecond;
        bool builtSecond = TryBuildHitKey(lateSecondAtkId, lateTargetId, out keySecond);
        if (keyFirst == null || KeySettled(keyFirst))
        {
            report.AppendLine("late hit check: the held key (seq " + KeySeq(keyFirst) + ")"
                + " was settled anyway (the production prefix got there first), so the ordering "
                + "experiment is not measurable this run");
            phase = Phase.SplitPlatforms;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        int beforeReplay = LedgerCount("SettledCount");
        report.AppendLine("late hit check: the held hit (seq " + KeySeq(keyFirst) + ")"
            + " still has no settlement while atkId=" + lateSecondAtkId
            + " already settled (ledger settled=" + beforeReplay + ")");
        string result = BeginKey(keyFirst);
        if (result == "Accepted") CommitKey(keyFirst);
        int afterReplay = LedgerCount("SettledCount");
        string hurtIdText = "?";
        try
        {
            Component hurt = lateTarget == null ? null : lateTarget.GetComponent(FindType("EnemyBaseHurt"));
            if (hurt != null) hurtIdText = Convert.ToString(Read(hurt, "hurtId"));
        }
        catch
        {
        }
        report.AppendLine("late hit check: settling the held key (the older attack) -> " + result
            + " ; ledger settled " + beforeReplay + " -> " + afterReplay
            + " ; target native hurtId=" + hurtIdText
            + " (a high-water mark at that hurtId would drop the held hit)");
        report.AppendLine("OBSERVE the earlier hit also carries the smaller native atkId="
            + (KeySeq(keyFirst) < KeySeq(keySecond)));
        Check("a real hit was held back while a later hit settled",
            deferredCaptured && beforeReplay >= lateSettledBefore + 1);
        Check("the held (older) hit is still accepted by the ledger", result == "Accepted");
        Check("both out-of-order hit keys are in the ledger",
            afterReplay == beforeReplay + 1 && KeySettled(keyFirst)
            && (!builtSecond || KeySettled(keySecond)));
        phase = Phase.SplitPlatforms;
        phaseAt = Time.realtimeSinceStartup;
    }

    /// <summary>
    /// Holds one real hit back: returning false skips every later prefix (including the
    /// production ledger prefix) and the native body, so the hit reaches the game only when the
    /// probe replays it. Raised to Priority.First so it runs ahead of the production prefix.
    /// </summary>
    private static bool DeferHurtPrefix(object __instance, object atkData, int atkId, object body, Vector2 hurtPos)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished) return true;
        if (probe.deferredReplaying || !probe.deferArmed) return true;
        bool first = !probe.deferredCaptured;
        // A second hitbox of the same swing re-enters with the same atkId; holding it too is the
        // point - otherwise it settles the same key that is supposed to arrive late.
        if (!first && atkId != probe.deferredAtkId) return true;
        if (first)
        {
            probe.deferredCaptured = true;
            probe.deferredInstance = __instance;
            probe.deferredAtkData = atkData;
            probe.deferredAtkId = atkId;
            probe.deferredBody = body;
            probe.deferredHurtPos = hurtPos;
        }
        // Prefix order is not guaranteed (the production ledger patch is installed first), so the
        // hook must also undo a settlement the production prefix already opened for this hit:
        // releasing the pending key is what actually keeps the hit unsettled.
        string released = "none";
        try
        {
            Component instance = __instance as Component;
            ulong targetId = probe.EntityIdOf(instance == null ? null : instance.gameObject);
            if (targetId != 0)
            {
                object key;
                if (probe.TryBuildHitKey(atkId, targetId, out key))
                {
                    probe.ReleaseKey(key);
                    if (first) probe.deferredKey = key;
                    released = "atkId=" + atkId + " target=" + targetId
                        + " seq=" + Convert.ToString(Read(key, "AttackSeq"));
                }
            }
            ClearCurrentSettlement();
        }
        catch
        {
        }
        if (probe.deferredHolds < 6)
        {
            probe.deferredHolds++;
            probe.report.AppendLine("late hit check: held atkId=" + atkId
                + " back before settlement (released pending settlement: " + released + ")");
        }
        return false;
    }

    /// <summary>
    /// V16 knockdown observation: records every PlayerAction state change per player so the probe
    /// can tell whether a player was knocked down (Fall1/Fall2) and got back up (GetUp).
    /// </summary>
    private void InstallStatePatch()
    {
        statePatchTried = true;
        Type actionType = FindType("PlayerAction");
        if (actionType == null) return;
        MethodInfo change = null;
        MethodInfo[] methods = actionType.GetMethods(InstanceFlags);
        for (int i = 0; i < methods.Length; i++)
        {
            if (methods[i].Name == "ChangeState" && methods[i].GetParameters().Length == 2)
            {
                change = methods[i];
                break;
            }
        }
        if (change == null)
        {
            report.AppendLine("PlayerAction.ChangeState was not found; knockdown observation is unavailable");
            return;
        }
        harmony.Patch(change, null,
            new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("ChangeStatePostfix", StaticFlags)));
        report.AppendLine("patched PlayerAction.ChangeState (knockdown observation)");
    }

    private static void ChangeStatePostfix(object __instance, object[] __args)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished || __args == null || __args.Length < 1 || __args[0] == null)
        {
            return;
        }
        string name = __args[0].ToString();
        try
        {
            if (probe.p1Action != null && object.ReferenceEquals(__instance, probe.p1Action))
            {
                if (!probe.p1ActionStates.Contains(name)) probe.p1ActionStates.Add(name);
            }
            else if (probe.p2Action != null && object.ReferenceEquals(__instance, probe.p2Action))
            {
                if (!probe.p2ActionStates.Contains(name)) probe.p2ActionStates.Add(name);
            }
        }
        catch
        {
        }
    }
    private void InstallCommitLogPatch()
    {
        commitLogTried = true;
        Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
        MethodInfo commit = router == null ? null : router.GetMethod("CommitHit", StaticFlags);
        if (commit == null) return;
        harmony.Patch(commit, null,
            new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("CommitKeyPostfix", StaticFlags)));
        report.AppendLine("patched CombatRouter.CommitHit (settlement log)");
    }

    private static void CommitKeyPostfix(object key)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished || key == null) return;
        if (probe.commitVetoed)
        {
            probe.commitVetoed = false;
            probe.report.AppendLine("[ledger] commit vetoed for the held hit");
            return;
        }
        if (probe.phase == Phase.P1LowProbe || probe.phase == Phase.P1HighProbe)
        {
            try
            {
                Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
                Type capture = FindType("IceyCoop.GameBridge.Combat.HurtOutcomeCapture");
                ulong attacker = Convert.ToUInt64(Read(key, "AttackerEntityId"));
                ulong targetId = Convert.ToUInt64(Read(key, "TargetEntityId"));
                ulong p1EntityId = Convert.ToUInt64(ReadStaticMember(router, "EntityIdP1"));
                object capturedTarget = ReadStaticMember(capture, "targetAttribute");
                bool hasBaseline = Convert.ToBoolean(ReadStaticMember(capture, "hasHpBaseline"));
                int hpBefore = Convert.ToInt32(ReadStaticMember(capture, "hpBefore"));
                object currentHpValue = Read(capturedTarget, "currentHp");
                int hpAfter = currentHpValue == null ? -1 : Convert.ToInt32(currentHpValue);
                bool sameTarget = probe.attrTarget != null && ReferenceEquals(capturedTarget, probe.attrTarget);
                bool decreased = hasBaseline && hpAfter >= 0 && hpAfter < hpBefore;
                Component capturedTargetComponent = capturedTarget as Component;
                probe.report.AppendLine("[p1-probe-settlement] attacker=" + attacker + " target=" + targetId
                    + " phase=" + probe.phase + " capturedTarget=" + (capturedTargetComponent == null ? "<null-or-non-component>" : capturedTargetComponent.gameObject.name)
                    + " sameFixtureTarget=" + sameTarget + " hasBaseline=" + hasBaseline
                    + " hp=" + hpBefore + "->" + hpAfter + " decreased=" + decreased);
                if (attacker == p1EntityId && sameTarget && decreased)
                {
                    if (probe.phase == Phase.P1LowProbe && !probe.p1LowOwnedSample)
                    {
                        probe.p1LowHpBefore = hpBefore;
                        probe.p1LowHpAfter = hpAfter;
                        probe.p1LowOwnedSample = true;
                    }
                    else if (probe.phase == Phase.P1HighProbe && !probe.p1HighOwnedSample)
                    {
                        probe.p1HighHpBefore = hpBefore;
                        probe.p1HighHpAfter = hpAfter;
                        probe.p1HighOwnedSample = true;
                    }
                }
            }
            catch (Exception ex)
            {
                probe.report.AppendLine("[p1-probe-settlement] capture failed: " + ex);
                probe.Check("P1 settlement capture metadata remained readable", false);
            }
        }
        if (probe.commitLogs >= 40) return;
        probe.commitLogs++;
        probe.report.AppendLine("[ledger] commit attacker=" + Convert.ToString(Read(key, "AttackerEntityId"))
            + " seq=" + Convert.ToString(Read(key, "AttackSeq"))
            + " target=" + Convert.ToString(Read(key, "TargetEntityId"))
            + " phase=" + probe.phase);
    }

    private void ReleaseKey(object key)
    {
        Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
        MethodInfo release = router == null ? null : router.GetMethod("ReleaseHit", StaticFlags);
        if (release != null) release.Invoke(null, new object[] { key });
    }

    private static void ClearCurrentSettlement()
    {
        try
        {
            Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
            if (router == null) return;
            FieldInfo flag = router.GetField("HasCurrentSettlement", StaticFlags);
            if (flag != null) flag.SetValue(null, false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// V08 (second attempt). The first attempt held the hit at EnemyBaseHurt.NormalHurt, but the
    /// production ledger prefix runs first and its postfix still committed the key, so the hit was
    /// never actually held. This vetoes the single commit site instead: CombatRouter.CommitHit has
    /// exactly one caller, so an interception there cannot lose a race with another prefix.
    /// </summary>
    private void InstallHurtDeferPatch()
    {
        hurtDeferTried = true;
        Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
        MethodInfo commit = router == null ? null : router.GetMethod("CommitHit", StaticFlags);
        if (commit == null)
        {
            report.AppendLine("CombatRouter.CommitHit was not found; the held-hit hook is unavailable");
            return;
        }
        harmony.Patch(commit, new HarmonyMethod(typeof(AttackOwnerProbe).GetMethod("VetoCommitPrefix", StaticFlags)));
        vetoHookOk = true;
        report.AppendLine("patched CombatRouter.CommitHit (held-hit veto)");
    }

    /// <summary>
    /// Refuses to commit the first real hit while the experiment is armed, and drops its pending
    /// key so the hit stays genuinely unsettled. Later hits commit normally.
    /// </summary>
    private static bool VetoCommitPrefix(object key)
    {
        AttackOwnerProbe probe = Current;
        if (probe == null || probe.finished || key == null) return true;
        if (probe.deferredReplaying || !probe.deferArmed) return true;
        if (probe.deferredCaptured) return true;
        probe.deferredCaptured = true;
        probe.deferredKey = key;
        probe.commitVetoed = true;
        string seq = Convert.ToString(Read(key, "AttackSeq"));
        probe.ReleaseKey(key);
        if (probe.deferredHolds < 6)
        {
            probe.deferredHolds++;
            probe.report.AppendLine("late hit check: vetoed the commit of the first real hit"
                + " (attacker=" + Convert.ToString(Read(key, "AttackerEntityId"))
                + " seq=" + seq + " target=" + Convert.ToString(Read(key, "TargetEntityId"))
                + "), its pending key was released so it stays unsettled");
        }
        return false;
    }



    private bool TryBuildHitKey(int atkId, ulong targetEntityId, out object key)
    {
        key = null;
        Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
        MethodInfo build = router == null ? null : router.GetMethod("TryBuildHitKey", StaticFlags);
        if (build == null) return false;
        object[] args = new object[] { atkId, targetEntityId, null };
        bool ok = Convert.ToBoolean(build.Invoke(null, args));
        key = args[2];
        return ok;
    }

    private string BeginKey(object key)
    {
        Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
        MethodInfo begin = router == null ? null : router.GetMethod("BeginHit", StaticFlags);
        if (begin == null) return "NoRouter";
        object result = begin.Invoke(null, new object[] { key });
        return Convert.ToString(result);
    }

    private void CommitKey(object key)
    {
        Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
        MethodInfo commit = router == null ? null : router.GetMethod("CommitHit", StaticFlags);
        if (commit != null) commit.Invoke(null, new object[] { key });
    }

    private int KeySeq(object key)
    {
        try
        {
            object seq = Read(key, "AttackSeq");
            return seq == null ? -1 : Convert.ToInt32(seq);
        }
        catch
        {
            return -1;
        }
    }
    private bool KeySettled(object key)
    {
        try
        {
            Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
            object ledger = router == null ? null : router.GetField("Ledger", StaticFlags).GetValue(null);
            if (ledger == null) return false;
            MethodInfo isSettled = ledger.GetType().GetMethod("IsSettled", InstanceFlags);
            return isSettled != null && Convert.ToBoolean(isSettled.Invoke(ledger, new object[] { key }));
        }
        catch
        {
            return false;
        }
    }

    private string SceneNameOf()
    {
        try
        {
            return Convert.ToString(ReadStaticMember(FindType("LevelManager"), "SceneName"));
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// V04: the two players stand on two different platforms (different ground heights) and each
    /// one is driven independently - P2 through the production remote path, P1 through the native
    /// button - so neither jump carries the other player.
    /// </summary>
    private void SplitPlatforms()
    {
        if (splitStage == 0)
        {
            Vector3 spotA;
            Vector3 spotB;
            if (!FindTwoStandSpots(out spotA, out spotB))
            {
                report.AppendLine("split platforms: no two platforms with a height difference were found"
                    + " (spots=" + splitSpotCount + ", best difference=" + F(splitHeightDiff) + ")");
                report.AppendLine("OBSERVE different platforms available in this room=False");
                splitStage = 9;
                phase = Phase.DashSplit;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            GameObject p2 = p2Root as GameObject;
            if (p1 == null || p2 == null)
            {
                splitStage = 9;
                phase = Phase.DashSplit;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            MoveActor(p1, spotA);
            MoveActor(p2, spotB);
            report.AppendLine("split platforms: P1 at (" + F(spotA.x) + "," + F(spotA.y) + ") P2 at ("
                + F(spotB.x) + "," + F(spotB.y) + ") height difference=" + F(Mathf.Abs(spotA.y - spotB.y)));
            splitStage = 1;
            splitStageAt = Time.realtimeSinceStartup;
            ResetSplitStableWait();
            return;
        }
        if (splitStage == 1)
        {
            if (!SplitActorsAreStable())
            {
                if (Time.realtimeSinceStartup - splitWaitStartedAt > 15f)
                {
                    Check("split fixture reached stable player landings before P2 jump", false);
                    report.AppendLine("split platforms: FIXTURE_BLOCKED before P2 jump; no stable landing within 15s");
                    splitStage = 9; phase = Phase.DashSplit; phaseAt = Time.realtimeSinceStartup;
                }
                return;
            }
            splitP1Y0 = PlayerY(1);
            splitP2Y0 = PlayerY(2);
            report.AppendLine("split platforms: settled at p1 y=" + F(splitP1Y0) + " p2 y=" + F(splitP2Y0));
            report.AppendLine("OBSERVE the two players stayed on platforms of different height="
                + (Mathf.Abs(splitP1Y0 - splitP2Y0) >= 0.9f) + " (diagnostic only; positions must first be stable)");
            splitP1Peak = splitP1Y0;
            splitP2Peak = splitP2Y0;
            splitP2JumpBefore = jumpCallsP2;
            InjectAction(1, true, 0f);
            pendingReleaseAt = Time.realtimeSinceStartup + 0.08f;
            splitStage = 2;
            splitStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (splitStage == 2)
        {
            float now = Time.realtimeSinceStartup;
            if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
            {
                InjectAction(1, false, 0f);
                pendingReleaseAt = 0f;
            }
            splitP1Peak = Mathf.Max(splitP1Peak, PlayerY(1));
            splitP2Peak = Mathf.Max(splitP2Peak, PlayerY(2));
            if (now - splitStageAt < 1.4f) return;
            report.AppendLine("split platforms: after P2's jump p2 y " + F(splitP2Y0) + " -> peak " + F(splitP2Peak)
                + " ; p1 y " + F(splitP1Y0) + " -> peak " + F(splitP1Peak));
            Check("P2 jumped from its own platform", splitP2Peak - splitP2Y0 >= 0.4f);
            Check("P2's jump did not move P1", Mathf.Abs(splitP1Peak - splitP1Y0) <= 0.25f);
            Check("P2's production jump dispatch reached PlayerJumpAbility.Jump", jumpCallsP2 > splitP2JumpBefore);
            InjectAction(0, false, 0f);
            if (splitWindowOverallAt <= 0f) splitWindowOverallAt = Time.realtimeSinceStartup;
            ResetSplitStableWait();
            splitStage = 5;
            return;
        }
        if (splitStage == 3)
        {
            float now = Time.realtimeSinceStartup;
            if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
            {
                SetRawButton("Jump", false);
                pendingReleaseAt = 0f;
            }
            splitP1Peak = Mathf.Max(splitP1Peak, PlayerY(1));
            splitP2Peak = Mathf.Max(splitP2Peak, PlayerY(2));
            if (!splitWindowContaminated)
            {
                string invalidReason = SplitWindowInvalidReason();
                if (!string.IsNullOrEmpty(invalidReason))
                {
                    splitWindowContaminated = true;
                    splitWindowInvalidReason = invalidReason;
                    SetRawButton("Jump", false);
                    pendingReleaseAt = 0f;
                    InjectAction(0, false, 0f);
                    Vector3 p1Now, p2Now;
                    float p1VyNow, p2VyNow;
                    TryGetActorMotion(1, out p1Now, out p1VyNow);
                    TryGetActorMotion(2, out p2Now, out p2VyNow);
                    report.AppendLine("split platforms: INVALID external-event window attempt=" + splitWindowAttempts
                        + " reason=" + invalidReason + " p1=" + F(p1Now.x) + "," + F(p1Now.y) + " vy=" + F(p1VyNow)
                        + " hp=" + PlayerHp(1) + " state=" + ActionStateName(1)
                        + " ; p2=" + F(p2Now.x) + "," + F(p2Now.y) + " vy=" + F(p2VyNow)
                        + " hp=" + PlayerHp(2) + " state=" + ActionStateName(2)
                        + " jumpCallsP1=" + jumpCallsP1 + " jumpCallsP2=" + jumpCallsP2
                        + " anchorResnaps=" + P2AnchorResnaps());
                    if (splitWindowAttempts >= 3 || now - splitWindowOverallAt >= 15f)
                    {
                        Check("a clean P1 jump isolation window was available within 3 attempts and 15 seconds", false);
                        report.AppendLine("split platforms: FIXTURE_BLOCKED; no clean window, preserving an inconclusive fixture failure");
                        splitStage = 4;
                        splitStageAt = now;
                    }
                    else
                    {
                        ResetSplitStableWait();
                        splitStage = 5;
                    }
                    return;
                }
            }
            if (now - splitStageAt < 1.4f) return;
            bool p1Rose = splitP1Peak - splitP1Y0 >= 0.4f;
            Vector3 p1Final, p2Final;
            float p1FinalVy, p2FinalVy;
            TryGetActorMotion(1, out p1Final, out p1FinalVy);
            TryGetActorMotion(2, out p2Final, out p2FinalVy);
            report.AppendLine("split platforms: after P1's native jump p1 y " + F(splitP1Y0) + " -> peak "
                + F(splitP1Peak) + " ; p2 y " + F(splitP2Y0) + " -> peak " + F(splitP2Peak)
                + " jumpCallsP1=" + jumpCallsP1 + " (before=" + splitWindowP1JumpCalls + ") jumpCallsP2="
                + jumpCallsP2 + " (before=" + splitWindowP2JumpCalls + ") hp=" + PlayerHp(1) + "/" + PlayerHp(2)
                + " states=" + ActionStateName(1) + "/" + ActionStateName(2)
                + " finalPosVy=" + F(p1Final.x) + "," + F(p1Final.y) + "/" + F(p1FinalVy) + " ; "
                + F(p2Final.x) + "," + F(p2Final.y) + "/" + F(p2FinalVy)
                + " anchorResnaps=" + P2AnchorResnaps() + " nativeJumpHeld=" + RawButtonPressed("Jump"));
            bool p1JumpCalled = jumpCallsP1 > splitWindowP1JumpCalls;
            bool p2JumpNotCalled = jumpCallsP2 == splitWindowP2JumpCalls;
            Check("P1 jumped from its own platform through the native button", p1Rose && p1JumpCalled && !IsHitAction(ActionStateName(1)));
            Check("P1's jump did not move P2", p1Rose && p1JumpCalled && Mathf.Abs(splitP2Peak - splitP2Y0) <= 0.25f);
            Check("no P2 PlayerJumpAbility.Jump callback occurred during the clean P1 window", p2JumpNotCalled);
            splitCallsP2Before = attackCallsP2;
            ResetP2Idle();
            InjectAction(0, true, 1f);
            pendingReleaseAt = Time.realtimeSinceStartup + 0.08f;
            splitStage = 4;
            splitStageAt = Time.realtimeSinceStartup;
            return;
        }
        if (splitStage == 5)
        {
            if (SplitActorsAreStable())
            {
                Vector3 p1, p2;
                float p1Vy, p2Vy;
                TryGetActorMotion(1, out p1, out p1Vy);
                TryGetActorMotion(2, out p2, out p2Vy);
                report.AppendLine("split platforms: stable landing reacquired after invalid window; p1=" + F(p1.x) + "," + F(p1.y)
                    + " vy=" + F(p1Vy) + " state=" + ActionStateName(1) + " ; p2=" + F(p2.x) + "," + F(p2.y)
                    + " vy=" + F(p2Vy) + " state=" + ActionStateName(2) + " elapsed="
                    + F(Time.realtimeSinceStartup - splitWindowOverallAt));
                BeginP1JumpWindow();
                return;
            }
            if (Time.realtimeSinceStartup - splitWindowOverallAt >= 15f || splitWindowAttempts >= 3)
            {
                Check("a clean P1 jump isolation window was available within 3 attempts and 15 seconds", false);
                report.AppendLine("split platforms: FIXTURE_BLOCKED; did not regain stable landing within the bounded window");
                splitStage = 4;
                splitStageAt = Time.realtimeSinceStartup;
            }
            return;
        }
        if (splitStage == 4)
        {
            float now = Time.realtimeSinceStartup;
            if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
            {
                InjectAction(0, false, 0f);
                pendingReleaseAt = 0f;
            }
            if (attackCallsP2 > splitCallsP2Before)
            {
                report.AppendLine("split platforms: P2's attack started from its own platform (calls "
                    + splitCallsP2Before + " -> " + attackCallsP2 + ")");
                Check("P2 could attack while standing away from P1", true);
                ReturnToArena();
                splitStage = 9;
                phase = Phase.DashSplit;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            if (now - splitLastPressAt > 1.2f && splitPressTries < 4)
            {
                splitPressTries++;
                splitLastPressAt = now;
                ResetP2Idle();
                InjectAction(0, true, 1f);
                pendingReleaseAt = now + 0.08f;
                report.AppendLine("split platforms: P2's attack press did not start anything, retry " + splitPressTries);
                return;
            }
            if (now - splitStageAt > 12f)
            {
                report.AppendLine("split platforms: P2's attack never started after 12s (calls " + splitCallsP2Before
                    + " -> " + attackCallsP2 + ")");
                report.AppendLine("OBSERVE P2 could attack while standing away from P1=False (P2 x=" + F(PlayerX(2))
                    + ", P1 x=" + F(PlayerX(1)) + "; cause unconfirmed; inspect owner/action/HP/anchor diagnostics)");
                ReturnToArena();
                splitStage = 9;
                phase = Phase.DashSplit;
                phaseAt = Time.realtimeSinceStartup;
            }
            return;
        }
    }

    /// <summary>Puts both players back next to the boss so the following phases keep working.</summary>
    private void ReturnToArena()
    {
        try
        {
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (p1 != null && attrTarget != null)
            {
                Vector3 t = ((Component)attrTarget).transform.position;
                MoveActor(p1, new Vector3(t.x - 0.9f, t.y, p1.transform.position.z));
            }
            if (attrTarget != null) PlaceP2NextTo(attrTarget);
            ResetP2Idle();
        }
        catch
        {
        }
    }


    private float PlayerX(byte playerId)
    {
        GameObject root = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"),
            playerId == 1 ? "P1Root" : "P2Root") as GameObject;
        return root == null ? -999f : root.transform.position.x;
    }

    private void MoveActor(GameObject actor, Vector3 spot)
    {
        if (actor == null) return;
        actor.transform.position = new Vector3(spot.x, spot.y, actor.transform.position.z);
        Rigidbody2D body = actor.GetComponent<Rigidbody2D>();
        if (body != null) body.velocity = Vector2.zero;
    }

    private float PlayerY(byte playerId)
    {
        GameObject root = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"),
            playerId == 1 ? "P1Root" : "P2Root") as GameObject;
        return root == null ? -999f : root.transform.position.y;
    }

    /// <summary>
    /// Scans a horizontal band around P1 with 2D raycasts and returns the two stand points with the
    /// largest ground-height difference, so "different platforms" is measured, not assumed.
    /// </summary>
    private bool FindTwoStandSpots(out Vector3 a, out Vector3 b)
    {
        a = Vector3.zero;
        b = Vector3.zero;
        splitSpotCount = 0;
        splitHeightDiff = 0f;
        try
        {
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (p1 == null) return false;
            Vector3 origin = p1.transform.position;
            List<Vector3> spots = new List<Vector3>();
            for (float x = origin.x - 22f; x <= origin.x + 22f; x += 1f)
            {
                RaycastHit2D hit = Physics2D.Raycast(new Vector2(x, origin.y + 8f), Vector2.down, 45f);
                if (hit.collider == null) continue;
                if (hit.collider.isTrigger) continue;
                spots.Add(new Vector3(x, hit.point.y + 0.15f, origin.z));
            }
            splitSpotCount = spots.Count;
            for (int i = 0; i < spots.Count; i++)
            {
                for (int j = i + 1; j < spots.Count; j++)
                {
                    float dx = Mathf.Abs(spots[i].x - spots[j].x);
                    if (dx < 3f) continue;
                    float dy = Mathf.Abs(spots[i].y - spots[j].y);
                    if (dy < 1.2f || dy > 3.5f) continue;
                    if (splitHeightDiff <= 0f || dy < splitHeightDiff)
                    {
                        splitHeightDiff = dy;
                        a = spots[i];
                        b = spots[j];
                    }
                }
            }
            report.AppendLine("split platforms: raycast stand points=" + splitSpotCount
                + " best height difference=" + F(splitHeightDiff));
            return splitHeightDiff >= 1.2f;
        }
        catch (Exception ex)
        {
            report.AppendLine("split platforms: raycast failed " + ex.Message);
            return false;
        }
    }

    private GameObject PlayerRoot(byte playerId)
    {
        Type context = FindType("IceyCoop.GameBridge.BridgeContext");
        return ReadStaticMember(context, playerId == 2 ? "P2Root" : "P1Root") as GameObject;
    }

    private bool TryGetActorMotion(byte playerId, out Vector3 position, out float velocityY)
    {
        GameObject root = PlayerRoot(playerId);
        Rigidbody2D body = root == null ? null : root.GetComponent<Rigidbody2D>();
        if (root == null || body == null)
        {
            position = Vector3.zero;
            velocityY = 0f;
            return false;
        }
        position = root.transform.position;
        velocityY = body.velocity.y;
        return true;
    }

    private int P2AnchorResnaps()
    {
        object value = ReadStaticMember(FindType("IceyCoop.GameBridge.Actors.P2Anchor"), "Resnaps");
        return value == null ? -1 : Convert.ToInt32(value);
    }

    private static bool IsSettledAction(string state)
    {
        if (string.IsNullOrEmpty(state) || state == "?") return false;
        string value = state.ToLowerInvariant();
        return value.IndexOf("jump", StringComparison.Ordinal) < 0
            && value.IndexOf("fall", StringComparison.Ordinal) < 0
            && value.IndexOf("underatk", StringComparison.Ordinal) < 0
            && value.IndexOf("hurt", StringComparison.Ordinal) < 0
            && value.IndexOf("getup", StringComparison.Ordinal) < 0
            && value.IndexOf("dash", StringComparison.Ordinal) < 0
            && value.IndexOf("attack", StringComparison.Ordinal) < 0;
    }

    private static bool IsHitAction(string state)
    {
        if (string.IsNullOrEmpty(state)) return true;
        string value = state.ToLowerInvariant();
        return value.IndexOf("underatk", StringComparison.Ordinal) >= 0
            || value.IndexOf("hurt", StringComparison.Ordinal) >= 0
            || value.IndexOf("hitground", StringComparison.Ordinal) >= 0
            || value.IndexOf("getup", StringComparison.Ordinal) >= 0;
    }

    private void ResetSplitStableWait()
    {
        splitStableFrames = 0;
        splitPreviousMotionValid = false;
        splitPreviousP1State = null;
        splitPreviousP2State = null;
        splitWaitStartedAt = Time.realtimeSinceStartup;
    }

    private bool SplitActorsAreStable()
    {
        Vector3 p1 = Vector3.zero, p2 = Vector3.zero;
        float p1Vy = 0f, p2Vy = 0f;
        string p1State = ActionStateName(1);
        string p2State = ActionStateName(2);
        bool motion = TryGetActorMotion(1, out p1, out p1Vy) && TryGetActorMotion(2, out p2, out p2Vy);
        bool candidate = motion && Mathf.Abs(p1Vy) <= 0.05f && Mathf.Abs(p2Vy) <= 0.05f
            && IsSettledAction(p1State) && IsSettledAction(p2State);
        if (candidate && splitPreviousMotionValid
            && Vector3.Distance(p1, splitPreviousP1) <= 0.04f
            && Vector3.Distance(p2, splitPreviousP2) <= 0.04f
            && p1State == splitPreviousP1State && p2State == splitPreviousP2State)
        {
            splitStableFrames++;
        }
        else
        {
            splitStableFrames = candidate ? 1 : 0;
        }
        splitPreviousP1 = p1;
        splitPreviousP2 = p2;
        splitPreviousP1State = p1State;
        splitPreviousP2State = p2State;
        splitPreviousMotionValid = candidate;
        return splitStableFrames >= 20;
    }

    private string SplitWindowInvalidReason()
    {
        int p1Hp = PlayerHp(1), p2Hp = PlayerHp(2);
        string p1State = ActionStateName(1), p2State = ActionStateName(2);
        int resnaps = P2AnchorResnaps();
        if (p1Hp != splitWindowP1Hp || p2Hp != splitWindowP2Hp)
            return "hp changed p1=" + splitWindowP1Hp + "->" + p1Hp + " p2=" + splitWindowP2Hp + "->" + p2Hp;
        if (IsHitAction(p1State) || IsHitAction(p2State))
            return "hit action state p1=" + p1State + " p2=" + p2State;
        if (resnaps < 0 || resnaps != splitWindowAnchorResnaps)
            return "P2Anchor.Resnaps changed " + splitWindowAnchorResnaps + "->" + resnaps;
        return null;
    }

    private void BeginP1JumpWindow()
    {
        Vector3 p1, p2;
        float p1Vy, p2Vy;
        if (!TryGetActorMotion(1, out p1, out p1Vy) || !TryGetActorMotion(2, out p2, out p2Vy))
        {
            Check("stable P1 jump isolation window has both player rigidbodies", false);
            splitStage = 4;
            return;
        }
        splitWindowAttempts++;
        splitP1Y0 = p1.y;
        splitP2Y0 = p2.y;
        splitP1Peak = splitP1Y0;
        splitP2Peak = splitP2Y0;
        splitWindowP1Hp = PlayerHp(1);
        splitWindowP2Hp = PlayerHp(2);
        splitWindowAnchorResnaps = P2AnchorResnaps();
        splitWindowP1JumpCalls = jumpCallsP1;
        splitWindowP2JumpCalls = jumpCallsP2;
        splitWindowP1State = ActionStateName(1);
        splitWindowP2State = ActionStateName(2);
        splitWindowContaminated = false;
        splitWindowInvalidReason = string.Empty;
        splitWindowStartedAt = Time.realtimeSinceStartup;
        report.AppendLine("split platforms: clean-window attempt=" + splitWindowAttempts
            + " p1=" + F(p1.x) + "," + F(p1.y) + " vy=" + F(p1Vy) + " hp=" + splitWindowP1Hp
            + " state=" + splitWindowP1State + " jumps=" + splitWindowP1JumpCalls
            + " ; p2=" + F(p2.x) + "," + F(p2.y) + " vy=" + F(p2Vy) + " hp=" + splitWindowP2Hp
            + " state=" + splitWindowP2State + " jumps=" + splitWindowP2JumpCalls
            + " anchorResnaps=" + splitWindowAnchorResnaps);
        SetRawButton("Jump", true);
        pendingReleaseAt = Time.realtimeSinceStartup + 0.10f;
        splitStage = 3;
        splitStageAt = Time.realtimeSinceStartup;
    }

    /// <summary>V16 evidence: which action state a player is in, and what an enemy projectile locks.</summary>
    private void SampleCombatEvidence()
    {
        try
        {
            string s1 = ActionStateName(1);
            string s2 = ActionStateName(2);
            if (!string.IsNullOrEmpty(s1) && s1 != "?") p1ActionStates.Add(s1);
            if (!string.IsNullOrEmpty(s2) && s2 != "?") p2ActionStates.Add(s2);
            float now = Time.realtimeSinceStartup;
            if (bulletSampleAt > 0f && now - bulletSampleAt < 0.2f) return;
            bulletSampleAt = now;
            Type bulletType = FindType("EnemyBullet");
            if (bulletType == null) return;
            UnityEngine.Object[] bullets = FindLive(bulletType);
            if (bullets == null) return;
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            GameObject p2 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P2Root") as GameObject;
            for (int i = 0; i < bullets.Length; i++)
            {
                if (bullets[i] == null) continue;
                bulletSamples++;
                Transform locked = Read(bullets[i], "player") as Transform;
                if (locked == null) { bulletLockedUnknown++; continue; }
                if (p1 != null && (locked == p1.transform || locked.IsChildOf(p1.transform))) bulletLockedP1++;
                else if (p2 != null && (locked == p2.transform || locked.IsChildOf(p2.transform))) bulletLockedP2++;
                else bulletLockedUnknown++;
            }
        }
        catch
        {
        }
    }

    private string ActionStateName(byte playerId)
    {
        try
        {
            object binding = EnsureBinding(playerId);
            object action = binding == null ? null : Read(binding, "Action");
            if (action == null) return "?";
            Type enumType = FindType("PlayerAction+StateEnum");
            if (enumType == null) return "?";
            for (Type type = action.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo[] fields = type.GetFields(InstanceFlags | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].FieldType != enumType) continue;
                    object value = fields[i].GetValue(action);
                    return value == null ? "?" : value.ToString();
                }
            }
        }
        catch
        {
        }
        return "?";
    }

    private void SetRawButton(string name, bool pressed)
    {
        Type game = FindType("Core.Input+Game");
        FieldInfo field = game == null ? null : game.GetField(name, StaticFlags);
        object button = field == null ? null : field.GetValue(null);
        if (button == null) return;
        FieldInfo raw = button.GetType().GetField("_isPressed", InstanceFlags);
        if (raw != null) raw.SetValue(button, pressed);
    }

    private bool RawButtonPressed(string name)
    {
        Type game = FindType("Core.Input+Game");
        FieldInfo field = game == null ? null : game.GetField(name, StaticFlags);
        object button = field == null ? null : field.GetValue(null);
        FieldInfo raw = button == null ? null : button.GetType().GetField("_isPressed", InstanceFlags);
        return raw != null && Convert.ToBoolean(raw.GetValue(button));
    }
    /// <summary>
    /// V04 dash half: P1 dashes through the native FlashAttack button, P2 dashes through the
    /// production remote path (action Dash), and neither move is carried to the other player.
    /// </summary>
    private void DashSplit()
    {
        if (dashStage == 0)
        {
            dashStage = 1;
            dashP1X0 = PlayerX(1);
            dashP2X0 = PlayerX(2);
            dashP1Peak = 0f;
            dashP2Peak = 0f;
            SetRawButton("FlashAttack", true);
            pendingReleaseAt = Time.realtimeSinceStartup + 0.10f;
            InjectAction(2, true, 1f);
            dashP2ReleaseAt = Time.realtimeSinceStartup + 0.08f;
            dashStageAt = Time.realtimeSinceStartup;
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
        {
            SetRawButton("FlashAttack", false);
            pendingReleaseAt = 0f;
        }
        if (dashP2ReleaseAt > 0f && now >= dashP2ReleaseAt)
        {
            InjectAction(2, false, 1f);
            dashP2ReleaseAt = 0f;
        }
        float dx1 = Mathf.Abs(PlayerX(1) - dashP1X0);
        float dx2 = Mathf.Abs(PlayerX(2) - dashP2X0);
        dashP1Peak = Mathf.Max(dashP1Peak, dx1);
        dashP2Peak = Mathf.Max(dashP2Peak, dx2);
        if (now - dashStageAt < 1.4f) return;
        report.AppendLine("dash split: p1 dx peak=" + F(dashP1Peak) + " p2 dx peak=" + F(dashP2Peak));
        if (dashP1Peak >= 1.0f)
        {
            Check("P1 dashed through the native button", true);
        }
        else
        {
            report.AppendLine("OBSERVE P1's native FlashAttack did not move it (dx=" + F(dashP1Peak) + ")");
        }
        if (dashP2Peak >= 1.0f)
        {
            Check("P2 dashed through the remote path", true);
        }
        else
        {
            report.AppendLine("OBSERVE P2's remote dash did not move it (dx=" + F(dashP2Peak)
                + "; cause unconfirmed; inspect owner/action/HP/anchor diagnostics)");
        }
        ReturnToArena();
        dashStage = 2;
        phase = Phase.BlockTest;
        phaseAt = Time.realtimeSinceStartup;
    }

    /// <summary>Scene survey: one line per scene with its enemies, projectile types and a pit scan.</summary>
    private void SurveyDump()
    {
        UnityEngine.Object[] enemies = FindLive(FindType("EnemyAttribute"));
        string names = "";
        if (enemies != null)
        {
            for (int i = 0; i < enemies.Length; i++)
            {
                Component e = enemies[i] as Component;
                names += (e == null || e.gameObject == null ? "?" : e.gameObject.name) + ",";
            }
        }
        report.AppendLine("SURVEY scene=" + scene + " enemies=[" + names + "]"
            + " EnemyBullet=" + CountLive("EnemyBullet")
            + " EnemyBulletLaucher=" + CountLive("EnemyBulletLaucher")
            + " DahalHurt=" + CountLive("DahalHurt")
            + " BossFocusAnim=" + CountLive("BossFocusAnim")
            + " BattleCheckPoint=" + CountLive("BattleCheckPoint")
            + " pit=" + FindPitX());
    }

    private string FindPitX()
    {
        try
        {
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (p1 == null) return "n/a";
            Vector3 origin = p1.transform.position;
            for (float x = origin.x - 45f; x <= origin.x + 45f; x += 1f)
            {
                RaycastHit2D hit = Physics2D.Raycast(new Vector2(x, origin.y + 8f), Vector2.down, 200f);
                if (hit.collider == null) return "void@" + F(x);
                if (hit.point.y < origin.y - 8f) return "drop@" + F(x) + "->" + F(hit.point.y);
            }
            return "none";
        }
        catch
        {
            return "err";
        }
    }
    private void BlockTest()
    {
        if (!blockInit)
        {
            blockInit = true;
            if (attrTarget == null)
            {
                report.AppendLine("block test skipped: no target enemy");
                phase = Phase.HurtReal;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            object attr = ((Component)attrTarget).GetComponent(FindType("EnemyAttribute"));
            if (attr == null)
            {
                report.AppendLine("block test skipped: enemy has no EnemyAttribute");
                phase = Phase.HurtReal;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            savedMonsterDefence = WriteEnemyDefence(attr, 1);
            blockHpBefore = EnemyHp((Component)attrTarget);
            PumpIntervalSeconds = 1.0f;
            report.AppendLine("block test: set monsterDefence=1 on " + ((Component)attrTarget).gameObject.name
                + " hp=" + blockHpBefore);
            PlaceP2NextTo(attrTarget);
            ResetP2Idle();
            attackBursts = 0;
            pendingReleaseAt = 0f;
            nextBurstAt = Time.realtimeSinceStartup;
            return;
        }
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        if (blockStage == 1)
        {
            // V10: the absorb window must not swallow later contacts. Once the defence is
            // restored the next real hit has to settle as Damage again.
            int damageNow;
            outcomes.TryGetValue(1, out damageNow);
            int hpNow = EnemyHp((Component)attrTarget);
            float since = Time.realtimeSinceStartup - blockConfirmAt;
            if (damageNow > blockConfirmDamage && hpNow >= 0 && blockConfirmHp >= 0 && hpNow < blockConfirmHp)
            {
                report.AppendLine("post-block window: a later hit resolved to Damage after " + F(since)
                    + "s, hp " + blockConfirmHp + " -> " + hpNow
                    + " (Damage outcomes " + blockConfirmDamage + " -> " + damageNow + ")");
                Check("a real hit still settles damage after the absorb window", true);
                blockStage = 2;
                phase = Phase.DodgeTest;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            if (since > 26f)
            {
                report.AppendLine("post-block window: no damage hit within 26s after the block (hp " + hpNow
                    + ", Damage outcomes " + blockConfirmDamage + " -> " + damageNow + ")");
                Check("a real hit still settles damage after the absorb window", false);
                blockStage = 2;
                phase = Phase.DodgeTest;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            PumpP2Attack();
            return;
        }
        int blocked;
        outcomes.TryGetValue(2, out blocked);
        if (blocked > 0)
        {
            blockHpAfter = EnemyHp((Component)attrTarget);
            report.AppendLine("block test: native roll blocked after " + F(elapsed) + "s, hp "
                + blockHpBefore + " -> " + blockHpAfter);
            blockObserved = true;
            report.AppendLine("OBSERVE a native block produced the Blocked outcome");
            // Keep the absorb window verdict separate from the block itself: close the
            // window and require the next real hit to damage again.
            int damageAtBlock;
            outcomes.TryGetValue(1, out damageAtBlock);
            blockConfirmDamage = damageAtBlock;
            blockConfirmHp = blockHpAfter;
            blockConfirmAt = Time.realtimeSinceStartup;
            blockStage = 1;
            RestoreEnemyDefence();
            attackBursts = 0;
            pendingReleaseAt = 0f;
            nextBurstAt = Time.realtimeSinceStartup;
            PumpIntervalSeconds = 1.1f;
            return;
        }
        if (elapsed > 32f)
        {
            blockHpAfter = EnemyHp((Component)attrTarget);
            report.AppendLine("block test: no block within 32s (hp " + blockHpBefore + " -> " + blockHpAfter + ")");
            report.AppendLine("OBSERVE no native block was produced (monsterDefence forced, state gate still refused)");
            RestoreEnemyDefence();
            phase = Phase.DodgeTest;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        PumpP2Attack();
    }

    private object WriteEnemyDefence(object enemyAttribute, int value)
    {
        // get_monsterDefence() reads EnemyAttribute.dynamicDefence (baseDefence is the static
        // part), so those are the fields that must move for CalculateMonsterDefence to fire.
        // monsterDefence = (int)(dynamicDefence * hardLevel); a stock C2L1S7 enemy has
        // hardLevel 0, which is why writing dynamicDefence alone still reads back as 0.
        try
        {
            object hard = Read(enemyAttribute, "hardLevel");
            if (hard is float)
            {
                if (savedHardLevel == null) savedHardLevel = hard;
                Write(enemyAttribute, "hardLevel", 1f);
            }
        }
        catch
        {
        }
        string[] names = new string[] { "dynamicDefence", "baseDefence", "currentDefence" };
        object saved = null;
        for (int i = 0; i < names.Length; i++)
        {
            try
            {
                object current = Read(enemyAttribute, names[i]);
                if (current == null || !(current is int)) continue;
                if (i == 0) saved = current;
                Write(enemyAttribute, names[i], i == 0 ? value : 0);
            }
            catch
            {
            }
        }
        try
        {
            object check = Read(enemyAttribute, "monsterDefence");
            report.AppendLine("block test: monsterDefence now " + Convert.ToString(check));
        }
        catch
        {
        }
        return saved;
    }

    private void RestoreEnemyDefence()
    {
        if (attrTarget == null || savedMonsterDefence == null)
        {
            return;
        }
        object attr = ((Component)attrTarget).GetComponent(FindType("EnemyAttribute"));
        if (attr == null) return;
        try { Write(attr, "dynamicDefence", savedMonsterDefence); } catch { }
        if (savedHardLevel != null)
        {
            try { Write(attr, "hardLevel", savedHardLevel); } catch { }
        }
    }

    /// <summary>
    /// Dodged outcome. DaoBrother overrides the hurt path and can side-step instead of taking
    /// damage; monsterSideStep is (int)(dynamicSideStep * hardLevel), so the probe raises
    /// hardLevel/dynamicSideStep on the Dao and keeps hitting it until one attack is stepped.
    /// </summary>
    private void DodgeTest()
    {
        if (!dodgeInit)
        {
            dodgeInit = true;
            PumpIntervalSeconds = 1.2f;
            // Prefer the enemy the probe can actually hit: the melee swings reliably land on
            // the boss, while the Dao kept whiffing because it moves out of the arc.
            if (attrTarget != null)
            {
                dodgeTarget = attrTarget;
            }
            else
            {
                UnityEngine.Object[] enemies = FindLive(FindType("EnemyAttribute"));
                if (enemies != null)
                {
                    for (int i = 0; i < enemies.Length; i++)
                    {
                        Component candidate = enemies[i] as Component;
                        if (candidate == null || candidate.gameObject == null) continue;
                        if (candidate.gameObject.name.IndexOf("Dao", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        dodgeTarget = candidate;
                        break;
                    }
                }
            }
            if (dodgeTarget == null)
            {
                report.AppendLine("dodge test skipped: no Dao enemy alive in the wave");
                phase = Phase.HurtReal;
                phaseAt = Time.realtimeSinceStartup;
                PumpIntervalSeconds = 0.7f;
                return;
            }
            object attr = ((Component)dodgeTarget).GetComponent(FindType("EnemyAttribute"));
            if (attr != null)
            {
                try
                {
                    Write(attr, "hardLevel", 1f);
                    Write(attr, "dynamicSideStep", 1);
                    Write(attr, "dynamicDefence", 0);
                    report.AppendLine("dodge test: target " + ((Component)dodgeTarget).gameObject.name
                        + " monsterSideStep=" + Convert.ToString(Read(attr, "monsterSideStep")));
                }
                catch
                {
                }
            }
            dodgeHpBefore = EnemyHp(dodgeTarget);
            report.AppendLine("dodge test: hp before " + dodgeHpBefore);
            PlaceP2NextTo(dodgeTarget);
            ResetP2Idle();
            attackBursts = 0;
            pendingReleaseAt = 0f;
            nextBurstAt = Time.realtimeSinceStartup;
            return;
        }
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        int dodged;
        outcomes.TryGetValue(3, out dodged);
        if (dodged > 0)
        {
            dodgeObserved = true;
            dodgeHpAfter = EnemyHp(dodgeTarget);
            report.AppendLine("dodge test: native side-step observed after " + F(elapsed) + "s, hp "
                + dodgeHpBefore + " -> " + dodgeHpAfter);
            phase = Phase.SkillIndep;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            PumpIntervalSeconds = 0.7f;
            return;
        }
        if (elapsed > 24f || dodgeTarget == null)
        {
            dodgeHpAfter = dodgeTarget == null ? -1 : EnemyHp(dodgeTarget);
            report.AppendLine("dodge test: no side-step within 24s (hp " + dodgeHpBefore + " -> " + dodgeHpAfter + ")");
            phase = Phase.SkillIndep;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            PumpIntervalSeconds = 0.7f;
            return;
        }
        PumpP2Attack();
    }

    /// <summary>
    /// V05: one player's skill must only spend that player's own energy. P2 casts through the
    /// production InputAdapter; P1's energy has to stay where it was.
    /// </summary>
    private void SkillIndep()
    {
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        if (skillStage == 0)
        {
            // Control window: give P1 energy and cast nothing. ICEY drains energy on its own,
            // so without this baseline a later drop cannot be blamed on P2's skill.
            skillStage = 1;
            SetPlayerEnergy(1, 5);
            SetPlayerEnergy(2, 10);
            skillStageAt = Time.realtimeSinceStartup;
            report.AppendLine("skill control: p1 energy set to " + PlayerEnergy(1));
            return;
        }
        if (skillStage == 1)
        {
            if (Time.realtimeSinceStartup - skillStageAt < 1.5f) return;
            skillE1Control = PlayerEnergy(1);
            report.AppendLine("skill control: p1 energy after 1.5s with no skill = " + skillE1Control
                + " (baseline decays on its own=" + (skillE1Control < 5) + ")");
            skillStage = 2;
            skillInit = true;
            skillAttempts = 0;
            StartSkillAttempt();
            return;
        }
        if (!skillReleased && Time.realtimeSinceStartup >= skillReleaseAt)
        {
            InjectAction(3, false, 0f);
            skillReleased = true;
        }
        skillE1After = PlayerEnergy(1);
        skillE2After = PlayerEnergy(2);
        bool p2Spent = skillE2Before >= 0 && skillE2After >= 0 && skillE2After < skillE2Before;
        // One injected skill frame can be lost (the input channel is shared), so retry the
        // stimulus before blaming the routing: the assertion is about what a delivered skill
        // costs, not about how lucky the injection was.
        if (!p2Spent && skillAttempts < 4 && Time.realtimeSinceStartup - skillAttemptAt > 3f)
        {
            report.AppendLine("skill test: attempt " + skillAttempts + " was not consumed (p2 energy "
                + skillE2Before + " -> " + skillE2After + "); re-injecting");
            StartSkillAttempt();
            return;
        }
        if (skillDirectDone && Time.realtimeSinceStartup - skillDirectAt > 1.0f)
        {
            report.AppendLine("skill diag: calls after direct invoke = " + skillCallCount
                + " energy p1=" + PlayerEnergy(1) + " p2=" + PlayerEnergy(2));
            phase = Phase.HurtReal;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        if (!skillDirectDone && (p2Spent || elapsed > 22f))
        {
            report.AppendLine("skill test: energy after p1=" + skillE1After + " p2=" + skillE2After
                + " (p2 spent=" + p2Spent + ")");
            if (skillCallCount > 0)
        {
            Check("P2 skill spent P2 energy", p2Spent);
        }
        else
        {
            // The injected skill frame never reached the ability, so there is no skill to charge
            // against: report it instead of pretending the routing was proven or broken.
            report.AppendLine("OBSERVE the injected skill never reached P2's PlayerSkillAbility.BladeStorm"
                + " (calls=0 after " + skillAttempts + " attempts), so the energy verdict is not attributable");
        }
            report.AppendLine("skill diag: calls after InputAdapter injection = " + skillCallCount);
            if (!skillDirectDone)
            {
                skillDirectDone = true;
                skillDirectAt = Time.realtimeSinceStartup;
                try
                {
                    MethodInfo blade = p2SkillAbility == null ? null
                        : p2SkillAbility.GetType().GetMethod("BladeStorm", InstanceFlags, null, new Type[0], null);
                    if (blade != null)
                    {
                        report.AppendLine("skill diag: invoking BladeStorm directly on P2 ability");
                        blade.Invoke(p2SkillAbility, null);
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine("skill diag: direct invoke threw " + ex.Message);
                }
            }
            return;
            // Only meaningful if the control window showed P1's pool holding still; ICEY drains
            // energy over time, which would otherwise look like a cross-player leak.
            if (skillE1Control >= 5)
            {
                Check("P2 skill did not touch P1 energy", skillE1Before >= 0 && skillE1After == skillE1Before);
            }
            else
            {
                report.AppendLine("OBSERVE energy decays on its own here, so the P1 drop is not attributable"
                    + " (control=" + skillE1Control + ", before=" + skillE1Before + ", after=" + skillE1After + ")");
            }
            phase = Phase.HurtReal;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
    }

    /// <summary>One skill stimulus: refill both pools, then inject a Skill press/release pair.</summary>
    private void StartSkillAttempt()
    {
        skillAttempts++;
        SetPlayerEnergy(1, 5);
        SetPlayerEnergy(2, 10);
        skillE1Before = PlayerEnergy(1);
        skillE2Before = PlayerEnergy(2);
        skillE1After = skillE1Before;
        skillE2After = skillE2Before;
        skillReleased = false;
        InjectAction(3, true, 1f);
        skillReleaseAt = Time.realtimeSinceStartup + 0.08f;
        skillAttemptAt = Time.realtimeSinceStartup;
        report.AppendLine("skill test: attempt " + skillAttempts + " energy before p1=" + skillE1Before
            + " p2=" + skillE2Before);
    }

    private void SetPlayerEnergy(byte playerId, int value)
    {
        object binding = EnsureBinding(playerId);
        object attribute = binding == null ? null : Read(binding, "Attribute");
        if (attribute == null) return;
        try
        {
            Write(attribute, "maxEnergy", Math.Max(value, 10));
            Write(attribute, "currentEnergy", value);
        }
        catch
        {
        }
    }

    private int PlayerEnergy(byte playerId)
    {
        object binding = EnsureBinding(playerId);
        object attribute = binding == null ? null : Read(binding, "Attribute");
        object energy = attribute == null ? null : Read(attribute, "currentEnergy");
        return energy is int ? (int)energy : -1;
    }

    private void HurtReal()
    {
        if (!hurtInit)
        {
            hurtInit = true;
            Component enemy = attrTarget != null ? attrTarget : PickToughestEnemy();
            if (enemy == null)
            {
                Check("a live enemy exists to attack the players", false);
                phase = Phase.Verify;
                return;
            }
            // Both on the same side: a boss that cleaves in front then damages both, instead
            // of turning to one of them and leaving the other untouched.
            PlacePlayerNextTo(1, enemy, 0.7f);
            PlacePlayerNextTo(2, enemy, 1.0f);
            p1HurtBefore = PlayerHp(1);
            p2HurtBefore = PlayerHp(2);
            report.AppendLine("parked both players next to " + enemy.gameObject.name
                + "; hp p1=" + p1HurtBefore + " p2=" + p2HurtBefore + " (waiting for the enemy's own attack)");
            return;
        }
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        int h1 = PlayerHp(1);
        int h2 = PlayerHp(2);
        // The boss knocks players away; walk them back so both stay in reach.
        if (elapsed > 2f && (int)(elapsed / 2f) != lastHurtReposition)
        {
            lastHurtReposition = (int)(elapsed / 2f);
            Component boss = attrTarget != null ? attrTarget : PickToughestEnemy();
            if (boss != null)
            {
                PlacePlayerNextTo(1, boss, 0.7f);
                PlacePlayerNextTo(2, boss, 1.0f);
            }
        }
        SampleCombatEvidence();
        bool d1 = p1HurtBefore >= 0 && h1 >= 0 && h1 < p1HurtBefore;
        bool d2 = p2HurtBefore >= 0 && h2 >= 0 && h2 < p2HurtBefore;
        if ((d1 && d2) || elapsed > 60f)
        {
            p1HurtAfter = h1;
            p2HurtAfter = h2;
            report.AppendLine("enemy attack result after " + F(elapsed) + "s: p1 " + p1HurtBefore + "->" + h1
                + " ; p2 " + p2HurtBefore + "->" + h2);
            // Pins on one player whenever the boss only cleaves in one direction, so the hard
            // assertion is "the attack reached a real victim"; hitting both is reported.
            Check("a real enemy attack damaged a routed victim", d1 || d2);
            report.AppendLine("OBSERVE both players were damaged by the boss=" + (d1 && d2)
                + " (p1=" + d1 + " p2=" + d2 + ")");
            phase = Phase.CameraSettle;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
    }

    private void PlacePlayerNextTo(byte playerId, Component enemy, float offset)
    {
        GameObject root = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"),
            playerId == 1 ? "P1Root" : "P2Root") as GameObject;
        if (root == null || enemy == null)
        {
            return;
        }
        Vector3 target = enemy.transform.position;
        root.transform.position = new Vector3(target.x + offset, target.y, root.transform.position.z);
        Rigidbody2D body = root.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.velocity = Vector2.zero;
        }
    }

    /// <summary>
    /// A real hit already went through T08LedgerPatches (BeginSettlement + CommitSettlement).
    /// Rebuilding the same key from the real nativeAtkId and asking the production ledger to
    /// open it again must be refused as Duplicate - that is what makes a duplicated native
    /// callback or a replayed packet harmless.
    /// </summary>
    /// <summary>
    /// V12 coordination half: two players cannot both hold the execution on the same boss.
    /// This exercises the production ExecutionRouter.Coordinator against the real boss entity
    /// id. It does not trigger the native QTE itself, so it proves the lock, not the animation.
    /// </summary>
    private void ExecutionLockCheck()
    {
        if (attrTarget == null)
        {
            report.AppendLine("execution lock check skipped: no boss/target entity");
            return;
        }
        try
        {
            ulong bossId = EntityIdOf(((Component)attrTarget).gameObject);
            Type router = FindType("IceyCoop.GameBridge.Combat.ExecutionRouter");
            object coord = router == null ? null : router.GetField("Coordinator", StaticFlags).GetValue(null);
            if (coord == null || bossId == 0)
            {
                report.AppendLine("execution lock check skipped: coordinator or entity id unavailable");
                return;
            }
            Type routerC = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
            uint epoch = routerC == null ? 1u
                : Convert.ToUInt32(routerC.GetField("WorldEpoch", StaticFlags).GetValue(null));
            MethodInfo tryAcquire = coord.GetType().GetMethod("TryAcquire", InstanceFlags);
            MethodInfo release = coord.GetType().GetMethod("Release", InstanceFlags);
            object[] a1 = new object[] { bossId, (byte)1, epoch, 0UL };
            bool first = Convert.ToBoolean(tryAcquire.Invoke(coord, a1));
            object[] a2 = new object[] { bossId, (byte)2, epoch, 0UL };
            bool second = Convert.ToBoolean(tryAcquire.Invoke(coord, a2));
            object holder = null;
            try { holder = coord.GetType().GetMethod("HolderOf", InstanceFlags).Invoke(coord, new object[] { bossId }); } catch { }
            if (first) { try { release.Invoke(coord, new object[] { a1[3] }); } catch { } }
            report.AppendLine("execution lock on boss " + bossId + ": P1=" + first + " P2=" + second
                + " holder=" + Convert.ToString(holder));
            Check("only one player can hold the execution on the same boss", first && !second);
            Check("the execution holder is the player that asked first", Convert.ToInt32(holder) == 1);
        }
        catch (Exception ex)
        {
            report.AppendLine("execution lock check threw: " + ex.Message);
            Check("only one player can hold the execution on the same boss", false);
        }
    }

    /// <summary>
    /// B4 red acceptance probe. Calls the real P2 PlayerExecuteAbility.GetEmenies(true)
    /// around the real execution coordinator lock and checks the live Carlos target is
    /// removed from, then restored to, the native candidate list. This does not invoke
    /// a QTE or claim that all GetExecuteEnemy side effects are vetoed.
    /// </summary>
    private void ExecutionCandidateGuardCheck()
    {
        Component boss = attrTarget;
        GameObject bossObject = boss == null ? null : boss.gameObject;
        Vector3 originalPosition = bossObject == null ? Vector3.zero : bossObject.transform.position;
        object bossAttribute = null;
        object savedAcceptAirExecute = null;
        bool acceptAirExecuteSaved = false;
        bool acquired = false;
        ulong bossId = 0;
        ulong token = 0;
        object coordinator = null;
        MethodInfo release = null;
        try
        {
            bool liveCarlos = bossObject != null
                && bossObject.name.IndexOf("Carlos", StringComparison.OrdinalIgnoreCase) >= 0
                && EnemyHp(boss) > 0;
            report.AppendLine("execution candidate guard target=" + (bossObject == null ? "<missing>" : bossObject.name)
                + " hp=" + (bossObject == null ? -1 : EnemyHp(boss)));
            Check("candidate guard uses the live Carlos attrTarget", liveCarlos);
            if (!liveCarlos) return;

            Type enemyAttributeType = FindType("EnemyAttribute");
            bossAttribute = enemyAttributeType == null ? null : bossObject.GetComponent(enemyAttributeType);
            Check("live Carlos EnemyAttribute is available", bossAttribute != null);
            if (bossAttribute == null) return;

            Type bridgeType = FindType("IceyCoop.GameBridge.BridgeContext");
            GameObject p2Root = bridgeType == null ? null : ReadStaticMember(bridgeType, "P2Root") as GameObject;
            Type playerAbilitiesType = FindType("PlayerAbilities");
            Component abilities = p2Root == null || playerAbilitiesType == null ? null : p2Root.GetComponent(playerAbilitiesType);
            object executeAbility = abilities == null ? null : Read(abilities, "execute");
            Check("real P2 PlayerAbilities.execute is available", executeAbility != null);
            if (executeAbility == null) return;

            object p2Binding = EnsureBinding(2);
            GameObject bindingRoot = p2Binding == null ? null : Read(p2Binding, "Root") as GameObject;
            object pab = Read(executeAbility, "pab");
            object pac = Read(executeAbility, "pac");
            ReportOwnerObject("execute.pab", pab, bindingRoot);
            ReportOwnerObject("execute.pac", pac, bindingRoot);
            report.AppendLine("execution candidate roots: P2Root=" + DescribeGameObject(p2Root)
                + " Binding.Root=" + DescribeGameObject(bindingRoot));

            Type rType = FindType("R");
            object rEnemy = rType == null ? null : ReadStaticMember(rType, "Enemy");
            object nativeEnemyAttributes = rEnemy == null ? null : Read(rEnemy, "EnemyAttributes");
            ReportNativeEnemyAttributes(nativeEnemyAttributes, bossAttribute, bossObject);

            MethodInfo getEnemies = executeAbility.GetType().GetMethod("GetEmenies", InstanceFlags);
            Check("native PlayerExecuteAbility.GetEmenies(bool) is available", getEnemies != null
                && getEnemies.GetParameters().Length == 1 && getEnemies.GetParameters()[0].ParameterType == typeof(bool));
            if (getEnemies == null || getEnemies.GetParameters().Length != 1
                || getEnemies.GetParameters()[0].ParameterType != typeof(bool)) return;

            FieldInfo acceptAirField = null;
            for (Type current = bossAttribute.GetType(); current != null && acceptAirField == null; current = current.BaseType)
            {
                acceptAirField = current.GetField("accpectAirExecute", InstanceFlags | BindingFlags.DeclaredOnly);
            }
            Check("Carlos accpectAirExecute field is available", acceptAirField != null && acceptAirField.FieldType == typeof(bool));
            if (acceptAirField == null || acceptAirField.FieldType != typeof(bool)) return;
            savedAcceptAirExecute = acceptAirField.GetValue(bossAttribute);
            acceptAirExecuteSaved = true;
            object deadBefore = Read(bossAttribute, "isDead");
            object rank = Read(bossAttribute, "rankType");
            report.AppendLine("candidate target BEFORE fixture: hp=" + EnemyHp(boss) + " isDead="
                + Convert.ToString(deadBefore) + " rankType=" + Convert.ToString(rank)
                + " accpectAirExecute=" + Convert.ToString(savedAcceptAirExecute)
                + " position=" + DescribeVector(bossObject.transform.position));

            Type routerType = FindType("IceyCoop.GameBridge.Combat.ExecutionRouter");
            FieldInfo coordinatorField = routerType == null ? null : routerType.GetField("Coordinator", StaticFlags);
            coordinator = coordinatorField == null ? null : coordinatorField.GetValue(null);
            MethodInfo tryAcquire = coordinator == null ? null : coordinator.GetType().GetMethod("TryAcquire", InstanceFlags);
            release = coordinator == null ? null : coordinator.GetType().GetMethod("Release", InstanceFlags);
            MethodInfo holderOf = coordinator == null ? null : coordinator.GetType().GetMethod("HolderOf", InstanceFlags);
            Check("execution coordinator acquisition, release, and holder methods are available",
                tryAcquire != null && release != null && holderOf != null);
            if (tryAcquire == null || release == null || holderOf == null) return;

            bossId = EntityIdOf(bossObject);
            Type combatRouterType = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
            FieldInfo epochField = combatRouterType == null ? null : combatRouterType.GetField("WorldEpoch", StaticFlags);
            uint epoch = epochField == null ? 1u : Convert.ToUInt32(epochField.GetValue(null));
            Check("Carlos has a production entity id", bossId != 0);
            if (bossId == 0) return;

            GameObject p2 = p2Root;
            Check("real P2 root is available for candidate fixture placement", p2 != null);
            if (p2 == null) return;

            // Put the real target within melee range of P2 and temporarily permit an
            // air execute, so the unguarded native search must first find this target.
            bossObject.transform.position = p2.transform.position + new Vector3(0.75f, 0f, 0f);
            acceptAirField.SetValue(bossAttribute, true);
            Transform pabTransform = ObjectTransform(pab);
            Vector3 nativeCenter = pabTransform == null ? p2.transform.position : pabTransform.position;
            Vector3 targetPosition = bossObject.transform.position;
            report.AppendLine("candidate target AFTER fixture: hp=" + EnemyHp(boss) + " isDead="
                + Convert.ToString(Read(bossAttribute, "isDead")) + " rankType=" + Convert.ToString(rank)
                + " accpectAirExecute=" + Convert.ToString(acceptAirField.GetValue(bossAttribute))
                + " position=" + DescribeVector(targetPosition)
                + " P2RootPosition=" + DescribeVector(p2.transform.position)
                + " nativeCenter=" + (pabTransform == null ? "<pab has no transform>" : DescribeVector(nativeCenter))
                + " distanceToP2Root=" + F(Vector3.Distance(targetPosition, p2.transform.position))
                + " distanceToNativeCenter=" + F(Vector3.Distance(targetPosition, nativeCenter)));
            object unheld = getEnemies.Invoke(executeAbility, new object[] { true });
            ReportNativeCandidateList("before-lock", unheld, bossAttribute, bossObject);
            bool presentBefore = CandidateListContains(unheld, bossAttribute, bossObject);
            report.AppendLine("native GetEmenies(true) before lock targetPresent=" + presentBefore);
            Check("native GetEmenies(true) finds the live Carlos target before a lock is held", presentBefore);
            if (!presentBefore) return;

            object[] acquireArgs = new object[] { bossId, (byte)1, epoch, 0UL };
            acquired = Convert.ToBoolean(tryAcquire.Invoke(coordinator, acquireArgs));
            token = Convert.ToUInt64(acquireArgs[3]);
            report.AppendLine("B4 coordinator acquire P1=" + acquired + " boss=" + bossId + " token=" + token);
            Check("P1 can acquire the Carlos execution lock for the controlled candidate fixture", acquired && token != 0);
            if (!acquired || token == 0) return;

            object whileHeld = getEnemies.Invoke(executeAbility, new object[] { true });
            ReportNativeCandidateList("while-P1-holds", whileHeld, bossAttribute, bossObject);
            bool presentWhileHeld = CandidateListContains(whileHeld, bossAttribute, bossObject);
            object holder = holderOf.Invoke(coordinator, new object[] { bossId });
            report.AppendLine("native GetEmenies(true) while P1 holds lock targetPresent=" + presentWhileHeld
                + " holder=" + Convert.ToString(holder));
            Check("native candidate search excludes Carlos while P1 holds its execution lock", !presentWhileHeld);
            Check("candidate search leaves P1 as the execution lock holder", Convert.ToInt32(holder) == 1);

            release.Invoke(coordinator, new object[] { token });
            acquired = false;
            object afterRelease = getEnemies.Invoke(executeAbility, new object[] { true });
            ReportNativeCandidateList("after-release", afterRelease, bossAttribute, bossObject);
            bool presentAfterRelease = CandidateListContains(afterRelease, bossAttribute, bossObject);
            report.AppendLine("native GetEmenies(true) after release targetPresent=" + presentAfterRelease);
            Check("native candidate search includes Carlos again after the lock is released", presentAfterRelease);
        }
        catch (Exception ex)
        {
            report.AppendLine("execution candidate guard threw: " + ex);
            Check("native Carlos execution candidate guard completes without an exception", false);
        }
        finally
        {
            if (acquired && coordinator != null && release != null && token != 0)
            {
                try { release.Invoke(coordinator, new object[] { token }); }
                catch (Exception ex) { report.AppendLine("candidate guard lock cleanup threw: " + ex.Message); }
            }
            if (acceptAirExecuteSaved && bossAttribute != null)
            {
                try
                {
                    Type type = bossAttribute.GetType();
                    FieldInfo field = null;
                    for (Type current = type; current != null && field == null; current = current.BaseType)
                    {
                        field = current.GetField("accpectAirExecute", InstanceFlags | BindingFlags.DeclaredOnly);
                    }
                    if (field != null) field.SetValue(bossAttribute, savedAcceptAirExecute);
                }
                catch (Exception ex) { report.AppendLine("candidate guard accept-air restore threw: " + ex.Message); }
            }
            if (bossObject != null)
            {
                try { bossObject.transform.position = originalPosition; }
                catch (Exception ex) { report.AppendLine("candidate guard position restore threw: " + ex.Message); }
            }
        }
    }

    private void ReportNativeCandidateList(string label, object list, object targetAttribute, GameObject targetObject)
    {
        System.Collections.IEnumerable values = list as System.Collections.IEnumerable;
        if (values == null)
        {
            report.AppendLine("NATIVE_CANDIDATES " + label + " <not enumerable> type=" + TypeName(list));
            return;
        }
        int count = 0;
        bool targetReferencePresent = false;
        foreach (object candidate in values)
        {
            count++;
            if (ReferenceEquals(candidate, targetAttribute)) targetReferencePresent = true;
            Component candidateComponent = candidate as Component;
            if (candidateComponent != null && ReferenceEquals(candidateComponent.gameObject, targetObject)) targetReferencePresent = true;
            report.AppendLine("NATIVE_CANDIDATE " + label + " index=" + (count - 1) + " " + DescribeNativeCandidate(candidate));
        }
        report.AppendLine("NATIVE_CANDIDATES " + label + " count=" + count
            + " CarlosAttributeReferenceEqualsPresent=" + targetReferencePresent);
    }

    private void ReportNativeEnemyAttributes(object list, object targetAttribute, GameObject targetObject)
    {
        System.Collections.IEnumerable values = list as System.Collections.IEnumerable;
        if (values == null)
        {
            report.AppendLine("R.Enemy.EnemyAttributes unavailable or non-enumerable type=" + TypeName(list));
            return;
        }
        int count = 0;
        bool targetReferencePresent = false;
        foreach (object item in values)
        {
            count++;
            if (ReferenceEquals(item, targetAttribute)) targetReferencePresent = true;
            Component component = item as Component;
            if (component != null && ReferenceEquals(component.gameObject, targetObject)) targetReferencePresent = true;
            report.AppendLine("R_ENEMY_ATTRIBUTE index=" + (count - 1) + " " + DescribeNativeCandidate(item));
        }
        report.AppendLine("R.Enemy.EnemyAttributes count=" + count
            + " CarlosAttributeReferenceEqualsPresent=" + targetReferencePresent);
    }

    private string DescribeNativeCandidate(object candidate)
    {
        Component component = candidate as Component;
        GameObject go = component == null ? candidate as GameObject : component.gameObject;
        if (go == null) return "type=" + TypeName(candidate) + " value=" + Convert.ToString(candidate);
        return "type=" + TypeName(candidate) + " name=" + go.name + " id=" + EntityIdOf(go)
            + " position=" + DescribeVector(go.transform.position);
    }

    private void ReportOwnerObject(string label, object value, GameObject expectedRoot)
    {
        Transform transform = ObjectTransform(value);
        GameObject actualRoot = transform == null ? null : transform.root.gameObject;
        report.AppendLine(label + " type=" + TypeName(value)
            + " root=" + DescribeGameObject(actualRoot)
            + " sameAsBindingRoot=" + (actualRoot != null && ReferenceEquals(actualRoot, expectedRoot)));
    }

    private static Transform ObjectTransform(object value)
    {
        Component component = value as Component;
        if (component != null) return component.transform;
        GameObject gameObject = value as GameObject;
        if (gameObject != null) return gameObject.transform;
        Transform transform = value as Transform;
        if (transform != null) return transform;
        object nested = Read(value, "gameObject");
        gameObject = nested as GameObject;
        return gameObject == null ? null : gameObject.transform;
    }

    private static string DescribeGameObject(GameObject value)
    {
        return value == null ? "<missing>" : value.name + "@" + DescribeVector(value.transform.position);
    }

    private static string DescribeVector(Vector3 value)
    {
        return "(" + F(value.x) + "," + F(value.y) + "," + F(value.z) + ")";
    }

    private static string TypeName(object value)
    {
        return value == null ? "<null>" : value.GetType().FullName;
    }

    private static bool CandidateListContains(object list, object targetAttribute, GameObject targetObject)
    {
        System.Collections.IEnumerable values = list as System.Collections.IEnumerable;
        if (values == null) throw new InvalidOperationException("GetEmenies did not return an enumerable candidate list");
        foreach (object candidate in values)
        {
            // GetEmenies returns List<GameObject>, rather than EnemyAttribute
            // or Component. Compare that native return type before components.
            GameObject gameObject = candidate as GameObject;
            if (gameObject != null && gameObject == targetObject) return true;
            if (ReferenceEquals(candidate, targetAttribute)) return true;
            Component component = candidate as Component;
            if (component != null && component.gameObject == targetObject) return true;
        }
        return false;
    }

    /// <summary>
    /// T16 camera restore. A boss room plays a focus animation that moves/zooms the camera;
    /// once the room settles the camera must be back on its baseline instead of being left
    /// focused on the boss.
    /// </summary>
    /// <summary>
    /// Give the follow camera a few seconds with both players standing still: a smooth camera
    /// needs time to catch up after the probe teleported the players, and only a camera that
    /// stays off the player after that is genuinely stuck.
    /// </summary>
    private void CameraSettle()
    {
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
        Camera live = Camera.main;
        if (p1 == null || live == null)
        {
            phase = Phase.Verify;
            return;
        }
        float d = Vector2.Distance(new Vector2(live.transform.position.x, live.transform.position.y),
            new Vector2(p1.transform.position.x, p1.transform.position.y));
        if (elapsed >= 1.5f && cameraSettleEarly < 0f)
        {
            cameraSettleEarly = d;
            report.AppendLine("camera settle: 2D distance at 1.5s = " + F(d));
        }
        if (elapsed < 3.5f)
        {
            return;
        }
        cameraSettleLate = d;
        report.AppendLine("camera settle: 2D distance at 3.5s = " + F(d));
        phase = Phase.Verify;
        phaseAt = Time.realtimeSinceStartup;
        respawnStage = 0;
    }

    private void CameraRestoreCheck()
    {
        if (!cameraBaselineSet)
        {
            report.AppendLine("camera check skipped: no baseline");
            return;
        }
        Camera live = Camera.main;
        if (live == null)
        {
            report.AppendLine("camera check skipped: no main camera");
            return;
        }
        float finalSizeDelta = Mathf.Abs(live.orthographicSize - cameraSizeBaseline);
        // The camera legitimately follows the player, so "restored" means "back on P1", not
        // "back on the exact spawn position" - compare against the live player instead.
        GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
        // A 2D camera sits far away on z by design, so only the x/y plane is meaningful.
        float toPlayer = p1 == null ? -1f
            : Vector2.Distance(new Vector2(live.transform.position.x, live.transform.position.y),
                               new Vector2(p1.transform.position.x, p1.transform.position.y));
        try
        {
            Camera[] all = Camera.allCameras;
            report.AppendLine("cameras: count=" + (all == null ? 0 : all.Length)
                + " p1=" + (p1 == null ? "?" : F(p1.transform.position.x) + "," + F(p1.transform.position.y)));
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    Camera one = all[i];
                    if (one == null) continue;
                    report.AppendLine("  camera[" + i + "] name=" + one.name + " tag=" + one.tag
                        + " depth=" + one.depth + " size=" + F(one.orthographicSize)
                        + " pos=" + F(one.transform.position.x) + "," + F(one.transform.position.y)
                        + " dist2D=" + (p1 == null ? "?" : F(Vector2.Distance(
                            new Vector2(one.transform.position.x, one.transform.position.y),
                            new Vector2(p1.transform.position.x, p1.transform.position.y)))));
                }
            }
        }
        catch
        {
        }
        report.AppendLine("camera: max deviation from spawn baseline=" + F(cameraMaxDeviation)
            + " max size delta=" + F(cameraMaxSizeDelta)
            + " distance to P1 at the end=" + F(toPlayer));
        report.AppendLine("OBSERVE camera moved well off the spawn baseline=" + (cameraMaxDeviation > 1.0f || cameraMaxSizeDelta > 0.5f));
        bool converged = cameraSettleEarly < 0f || cameraSettleLate <= cameraSettleEarly + 1.5f;
        report.AppendLine("camera convergence: early=" + F(cameraSettleEarly) + " late=" + F(cameraSettleLate)
            + " converged=" + converged + " (observation only - a live boss shifts the frame)");
        Check("the camera settled back on the player after the boss focus",
            toPlayer >= 0f && toPlayer < 12f && finalSizeDelta < 0.5f);
    }

    private ulong EntityIdOf(GameObject go)
    {
        try
        {
            Type adapter = FindType("IceyCoop.GameBridge.Combat.EntityAdapter");
            if (adapter == null || go == null) return 0;
            object kindEnemy = adapter.GetField("KindEnemy", StaticFlags).GetValue(null);
            MethodInfo getOrAssign = adapter.GetMethod("GetOrAssignId", StaticFlags);
            return Convert.ToUInt64(getOrAssign.Invoke(null, new object[] { go, kindEnemy, "probe" }));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// V17 reward de-duplication: one killed enemy must pay out once. The probe asks the
    /// production RewardRouter for the same drop twice; the second ask must be refused no
    /// matter whether the real death had already granted it.
    /// </summary>
    private void RewardDedupCheck()
    {
        if (clearedEnemyId == 0)
        {
            report.AppendLine("reward dedup check skipped: no cleared enemy id");
            return;
        }
        try
        {
            Type router = FindType("IceyCoop.GameBridge.Combat.RewardRouter");
            if (router == null) return;
            MethodInfo tryGrant = router.GetMethod("TryGrant", StaticFlags);
            int sourceKind = 1;
            try
            {
                object kindValue = ReadStaticMember(router, "SourceKindEnemyDrop");
                if (kindValue != null) sourceKind = Convert.ToInt32(kindValue);
            }
            catch
            {
            }
            object ledger = router.GetField("Ledger", StaticFlags).GetValue(null);
            int before = Convert.ToInt32(Read(ledger, "Count"));
            bool first = Convert.ToBoolean(tryGrant.Invoke(null, new object[] { sourceKind, clearedEnemyId, (byte)1, 1 }));
            bool second = Convert.ToBoolean(tryGrant.Invoke(null, new object[] { sourceKind, clearedEnemyId, (byte)1, 1 }));
            int after = Convert.ToInt32(Read(ledger, "Count"));
            report.AppendLine("reward grant for enemy " + clearedEnemyId + ": first=" + first + " second=" + second
                + " ledger " + before + "->" + after);
            Check("a replayed reward grant for the same source is refused", !second);
            Check("the reward ledger only grew once for one source", after - before <= 1);
        }
        catch (Exception ex)
        {
            report.AppendLine("reward dedup check threw: " + ex.Message);
            Check("a replayed reward grant for the same source is refused", false);
        }
    }

    private void DuplicateSettlementCheck()
    {
        int atkId = lastP2AtkId;
        if (atkId == 0 || targetEnemy == null)
        {
            report.AppendLine("duplicate settlement check skipped: atkId=" + atkId);
            return;
        }
        try
        {
            Type adapter = FindType("IceyCoop.GameBridge.Combat.EntityAdapter");
            Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
            if (adapter == null || router == null)
            {
                return;
            }
            object kindEnemy = adapter.GetField("KindEnemy", StaticFlags).GetValue(null);
            MethodInfo getOrAssign = adapter.GetMethod("GetOrAssignId", StaticFlags);
            ulong targetId = Convert.ToUInt64(getOrAssign.Invoke(null,
                new object[] { ((Component)targetEnemy).gameObject, kindEnemy, "probe" }));
            object[] args = new object[] { atkId, targetId, null };
            MethodInfo build = router.GetMethod("TryBuildHitKey", StaticFlags);
            if (build == null || !Convert.ToBoolean(build.Invoke(null, args)))
            {
                report.AppendLine("duplicate settlement check: key could not be rebuilt atkId=" + atkId);
                return;
            }
            object ledger = router.GetField("Ledger", StaticFlags).GetValue(null);
            MethodInfo begin = ledger.GetType().GetMethod("BeginSettlement", InstanceFlags);
            object result = begin.Invoke(ledger, new object[] { args[2] });
            string name = Convert.ToString(result);
            report.AppendLine("re-open a real hit key (atkId=" + atkId + " target=" + targetId + ") -> " + name);
            Check("a real, already-settled hit key is refused as duplicate", name == "Duplicate");
        }
        catch (Exception ex)
        {
            report.AppendLine("duplicate settlement check threw: " + ex.Message);
            Check("a real, already-settled hit key is refused as duplicate", false);
        }
    }

    private int CountLive(string typeName)
    {
        Type type = FindType(typeName);
        if (type == null) return -1;
        try
        {
            UnityEngine.Object[] found = FindLive(type);
            return found == null ? 0 : found.Length;
        }
        catch
        {
            return -1;
        }
    }

    private int LedgerCount(string member)
    {
        try
        {
            Type router = FindType("IceyCoop.GameBridge.Combat.CombatRouter");
            object ledger = router == null ? null : router.GetField("Ledger", StaticFlags).GetValue(null);
            if (ledger == null) return -1;
            object value = Read(ledger, member);
            return value is int ? (int)value : -1;
        }
        catch
        {
            return -1;
        }
    }

    private int BoostPlayerHp(byte playerId)
    {
        object binding = EnsureBinding(playerId);
        object attribute = binding == null ? null : Read(binding, "Attribute");
        if (attribute == null)
        {
            return -1;
        }
        object hp = Read(attribute, "currentHP");
        int original = hp is int ? (int)hp : -1;
        try
        {
            object maxHp = Read(attribute, "maxHP");
            if (maxHp is int) Write(attribute, "maxHP", 9999);
            Write(attribute, "currentHP", 9999);
        }
        catch
        {
        }
        return original;
    }

    private void RestorePlayerHp(byte playerId, int original)
    {
        if (original < 0)
        {
            return;
        }
        object binding = EnsureBinding(playerId);
        object attribute = binding == null ? null : Read(binding, "Attribute");
        if (attribute == null)
        {
            return;
        }
        try { Write(attribute, "currentHP", original); } catch { }
    }

    private int PlayerHp(byte playerId)
    {
        object binding = EnsureBinding(playerId);
        object attribute = binding == null ? null : Read(binding, "Attribute");
        object hp = attribute == null ? null : Read(attribute, "currentHP");
        return hp is int ? (int)hp : -1;
    }

    /// <summary>
    /// Controlled attribute experiment: the same high-HP enemy is hit twice by P2, first
    /// with Enhancement.Attack=0 and then with 3 (the native 0..3 scale). If the damage
    /// per hit changes with the attacker's own attribute, the settlement really reads the
    /// attacker instead of a fixed player.
    /// </summary>
    private void AttrA()
    {
        if (!attrLowInit)
        {
            attrLowInit = true;
            object enh = EnsureEnhancement(2);
            if (enh == null)
            {
                Check("P2 enhancement is available for the attribute experiment", false);
                phase = Phase.AttrB;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            if (executionCandidateOnly)
            {
                ResetP2Idle();
                attrTarget = PickToughestEnemy();
                if (attrTarget == null)
                {
                    Check("candidate-only fixture has a live enemy target", false);
                    Finish();
                    return;
                }
                PlaceP2NextTo(attrTarget);
                report.AppendLine("MODE fixture acquired P2 and attrTarget=" + attrTarget.gameObject.name
                    + "; running only the native execution candidate guard now.");
                ExecutionCandidateGuardCheck();
                Finish();
                return;
            }
            SetAllAtkFields(enh, 0);
            savedAttackP2 = savedAtkFields.ContainsKey("Attack") ? savedAtkFields["Attack"] : Read(enh, "Attack");
            ResetP2Idle();
            attrTarget = PickToughestEnemy();
            if (attrTarget == null)
            {
                Check("a high-HP enemy exists for the attribute experiment", false);
                Write(enh, "Attack", savedAttackP2);
                phase = Phase.AttrB;
                phaseAt = Time.realtimeSinceStartup;
                return;
            }
            attrLowHpBefore = EnemyHp(attrTarget);
            ParseEnhancement(enh);
            report.AppendLine("attr experiment: P2 Enhancement.Attack=" + Convert.ToString(Read(enh, "Attack"))
                + " target=" + attrTarget.gameObject.name + " hp=" + attrLowHpBefore);
            PlaceP2NextTo(attrTarget);
            attackBursts = 0;
            pendingReleaseAt = 0f;
            nextBurstAt = Time.realtimeSinceStartup;
            return;
        }
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        int hp = EnemyHp(attrTarget);
        if (attrLowHpBefore >= 0 && hp >= 0 && hp < attrLowHpBefore)
        {
            attrLowHpAfter = hp;
            report.AppendLine("attr: Enhancement.Attack=0 dealt " + (attrLowHpBefore - hp));
            p1ProbeInit = false;
            phase = Phase.P1LowProbe;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        if (elapsed > 12f)
        {
            attrLowHpAfter = hp;
            report.AppendLine("attr: Enhancement.Attack=0 dealt nothing within 12s (hp=" + hp + ")");
            p1ProbeInit = false;
            phase = Phase.P1LowProbe;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        PumpP2Attack();
    }

    private void AttrB()
    {
        if (!attrHighInit)
        {
            attrHighInit = true;
            object enh = EnsureEnhancement(2);
            if (enh != null && attrTarget != null)
            {
                SetAllAtkFields(enh, 3);
                ResetP2Idle();
                attrHighHpBefore = EnemyHp(attrTarget);
                report.AppendLine("attr experiment: P2 Enhancement.Attack=" + Convert.ToString(Read(enh, "Attack"))
                    + " same target hp=" + attrHighHpBefore);
                PlaceP2NextTo(attrTarget);
                attackBursts = 0;
                pendingReleaseAt = 0f;
                nextBurstAt = Time.realtimeSinceStartup;
            }
            return;
        }
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        int hp = EnemyHp(attrTarget);
        if (attrHighHpBefore >= 0 && hp >= 0 && hp < attrHighHpBefore)
        {
            attrHighHpAfter = hp;
            report.AppendLine("attr: Enhancement.Attack=3 dealt " + (attrHighHpBefore - hp));
            // Keep P2's buff in place for one more hit: this is the window in which P1 must
            // still settle with its own attributes (RestoreAttack happens after it).
            p1ProbeInit = false;
            phase = Phase.P1HighProbe;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        if (elapsed > 12f)
        {
            attrHighHpAfter = hp;
            report.AppendLine("attr: Enhancement.Attack=3 dealt nothing within 12s (hp=" + hp + ")");
            p1ProbeInit = false;
            phase = Phase.P1HighProbe;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        PumpP2Attack();
    }

    private void PumpP2Attack()
    {
        float now = Time.realtimeSinceStartup;
        if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
        {
            InjectAction(0, false, 0f);
            pendingReleaseAt = 0f;
            return;
        }
        if (now >= nextBurstAt && attackBursts < 40)
        {
            attackBursts++;
            Component pumpTarget = dodgeTarget != null && phase == Phase.DodgeTest ? dodgeTarget : attrTarget;
            if (pumpTarget != null) PlaceP2NextTo(pumpTarget);
            ResetP2Idle();
            float towards = ((Component)pumpTarget).transform.position.x
                >= ((GameObject)p2Root).transform.position.x ? 1f : -1f;
            InjectAction(0, true, towards);
            pendingReleaseAt = now + 0.08f;
            nextBurstAt = now + PumpIntervalSeconds;
        }
    }

    private void PlaceP2NextTo(Component enemy)
    {
        GameObject p2 = p2Root as GameObject;
        if (enemy == null || p2 == null)
        {
            return;
        }
        Vector3 target = enemy.transform.position;
        float side = target.x >= p2.transform.position.x ? -1f : 1f;
        p2.transform.position = new Vector3(target.x + side * 0.9f, target.y, p2.transform.position.z);
        Rigidbody2D body = p2.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.velocity = Vector2.zero;
        }
    }

    private Component PickToughestEnemy()
    {
        UnityEngine.Object[] enemies = FindLive(FindType("EnemyAttribute"));
        Component best = null;
        int bestHp = -1;
        if (enemies != null)
        {
            for (int i = 0; i < enemies.Length; i++)
            {
                Component c = enemies[i] as Component;
                if (c == null) continue;
                int hp = EnemyHp(c);
                if (hp > bestHp) { bestHp = hp; best = c; }
            }
        }
        return best;
    }

    private object EnsureEnhancement(byte playerId)
    {
        Type context = FindType("IceyCoop.GameBridge.BridgeContext");
        MethodInfo tryGet = context == null ? null : context.GetMethod("TryGetEnhancementForOwner", StaticFlags);
        if (tryGet == null) return null;
        object[] args = new object[] { playerId, null, null };
        return Convert.ToBoolean(tryGet.Invoke(null, args)) ? args[1] : null;
    }

    private void ParseEnhancement(object enh)
    {
    }

    private void SetAllAtkFields(object enh, int value)
    {
        for (int i = 0; i < AtkFields.Length; i++)
        {
            string name = AtkFields[i];
            try
            {
                object current = Read(enh, name);
                if (current == null) continue;
                if (!savedAtkFields.ContainsKey(name)) savedAtkFields[name] = current;
                Write(enh, name, Convert.ChangeType(value, current.GetType()));
            }
            catch
            {
            }
        }
    }

    private void RestoreAttack()
    {
        object enh = EnsureEnhancement(2);
        if (enh == null) return;
        foreach (KeyValuePair<string, object> pair in savedAtkFields)
        {
            try { Write(enh, pair.Key, pair.Value); } catch { }
        }
    }

    private void ResetP2Idle()
    {
        try
        {
            object binding = EnsureBinding(2);
            object action = binding == null ? null : Read(binding, "Action");
            if (action == null) return;
            Type enumType = FindType("PlayerAction+StateEnum");
            MethodInfo change = enumType == null ? null : action.GetType().GetMethod("ChangeState", InstanceFlags,
                null, new Type[] { enumType, typeof(float) }, null);
            if (change != null) change.Invoke(action, new object[] { Enum.Parse(enumType, "Idle"), 1f });
        }
        catch
        {
        }
    }

    private void P1Attack()
    {
        float elapsed = Time.realtimeSinceStartup - phaseAt;
        int p1Hits = 0;
        eventTriggerByOwner.TryGetValue(1, out p1Hits);
        if (p1Hits > 0)
        {
            if (targetEnemy != null) p1HpAfter = EnemyHp(targetEnemy as Component);
            report.AppendLine("P1 EventTrigger observed after " + F(elapsed) + "s; target hp " + p1HpBefore + " -> " + p1HpAfter);
            phase = Phase.AttrA;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        if (!p1Positioned)
        {
            GameObject p1 = ReadStaticMember(FindType("IceyCoop.GameBridge.BridgeContext"), "P1Root") as GameObject;
            if (p1 != null && targetEnemy != null)
            {
                Vector3 target = ((Component)targetEnemy).transform.position;
                // Put P1 on top of the enemy: the attack hitbox is centred on the
                // player, so the hit lands no matter which way P1 faces.
                p1.transform.position = new Vector3(target.x - 0.15f, target.y, p1.transform.position.z);
                Rigidbody2D body = p1.GetComponent<Rigidbody2D>();
                if (body != null) body.velocity = Vector2.zero;
                p1Positioned = true;
                p1HpBefore = EnemyHp((Component)targetEnemy);
                report.AppendLine("placed P1 at (" + F(p1.transform.position.x) + "," + F(p1.transform.position.y) + ") hpBefore=" + p1HpBefore);
            }
        }
        if (elapsed > 14f)
        {
            Check("P1 attack reached PlayerAtk.EventTrigger", false);
            SetRawButton(false);
            phase = Phase.AttrA;
            phaseAt = Time.realtimeSinceStartup;
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (pendingReleaseAt > 0f && now >= pendingReleaseAt)
        {
            SetRawButton(false);
            pendingReleaseAt = 0f;
            return;
        }
        if (now >= nextBurstAt && p1PressedBursts < 6)
        {
            p1PressedBursts++;
            SetRawButton(true);
            pendingReleaseAt = now + 0.10f;
            nextBurstAt = now + 1.1f;
            report.AppendLine("injected P1 Atk press burst " + p1PressedBursts);
        }
    }

    private static bool LooksLikeDownState(string state)
    {
        if (string.IsNullOrEmpty(state)) return false;
        string s = state.ToLowerInvariant();
        return s.Contains("down") || s.Contains("getup") || s.Contains("fall") || s.Contains("knock")
            || s.Contains("lie");
    }
    private void SetRawButton(bool pressed)
    {
        Type game = FindType("Core.Input+Game");
        FieldInfo field = game == null ? null : game.GetField("Atk", StaticFlags);
        object button = field == null ? null : field.GetValue(null);
        if (button == null)
        {
            return;
        }
        FieldInfo raw = button.GetType().GetField("_isPressed", InstanceFlags);
        if (raw != null)
        {
            raw.SetValue(button, pressed);
        }
    }

    private void InjectAction(byte action, bool pressed, float horizontal)
    {
        Type frameType = FindType("IceyCoop.Protocol.InputFrame");
        Type edgeType = FindType("IceyCoop.Protocol.InputEdge");
        Type adapterType = FindType("IceyCoop.GameBridge.Network.InputAdapter");
        if (frameType == null || edgeType == null || adapterType == null)
        {
            throw new InvalidOperationException("InputFrame/InputEdge/InputAdapter types missing");
        }
        object frame = Activator.CreateInstance(frameType);
        nextSeq++;
        Write(frame, "InputSeq", nextSeq);
        Write(frame, "Horizontal", horizontal);
        Write(frame, "Vertical", 0f);
        Write(frame, "HeldBits", (ushort)(pressed ? (1 << action) : 0));
        Array edges = Array.CreateInstance(edgeType, 1);
        object edge = Activator.CreateInstance(edgeType);
        Write(edge, "Ordinal", (byte)1);
        Write(edge, "Action", action);
        Write(edge, "Pressed", pressed);
        edges.SetValue(edge, 0);
        Write(frame, "Edges", edges);
        MethodInfo apply = adapterType.GetMethod("ApplyRemoteInput", StaticFlags);
        if (apply == null)
        {
            throw new MissingMethodException("InputAdapter.ApplyRemoteInput");
        }
        apply.Invoke(null, new object[] { (byte)2, frame });
    }

    private bool TryResolveOwner(object instance, out byte owner)
    {
        owner = 255;
        Type context = FindType("IceyCoop.GameBridge.BridgeContext");
        MethodInfo tryResolve = context == null ? null : context.GetMethod("TryResolveOwnerByRoot", StaticFlags);
        if (tryResolve == null)
        {
            return false;
        }
        object[] args = new object[] { instance, (byte)0 };
        object result = tryResolve.Invoke(null, args);
        owner = (byte)args[1];
        return result != null && Convert.ToBoolean(result);
    }

    private object EnsureBinding(byte playerId)
    {
        Type context = FindType("IceyCoop.GameBridge.BridgeContext");
        MethodInfo ensure = context == null ? null : context.GetMethod("EnsureBinding", StaticFlags);
        return ensure == null ? null : ensure.Invoke(null, new object[] { playerId });
    }

    private int EnemyHp(Component enemy)
    {
        if (enemy == null)
        {
            return -1;
        }
        string[] names = new string[] { "currentHP", "hp", "HP", "currentHp" };
        for (int i = 0; i < names.Length; i++)
        {
            object value = Read(enemy, names[i]);
            if (value is int)
            {
                return (int)value;
            }
        }
        Component hurt = enemy.GetComponent(FindType("EnemyBaseHurt"));
        if (hurt != null)
        {
            object value = Read(hurt, "hp");
            if (value is int)
            {
                return (int)value;
            }
        }
        return -1;
    }

    private static UnityEngine.Object[] FindLive(Type type)
    {
        if (type == null)
        {
            return new UnityEngine.Object[0];
        }
        UnityEngine.Object[] direct = UnityEngine.Object.FindObjectsOfType(type);
        if (direct != null && direct.Length > 0)
        {
            return direct;
        }
        try
        {
            UnityEngine.Object[] all = UnityEngine.Resources.FindObjectsOfTypeAll(type);
            List<UnityEngine.Object> live = new List<UnityEngine.Object>();
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component;
                if (c == null)
                {
                    continue;
                }
                try
                {
                    if (c.gameObject.scene.IsValid())
                    {
                        live.Add(all[i]);
                    }
                }
                catch
                {
                }
            }
            return live.ToArray();
        }
        catch
        {
            return new UnityEngine.Object[0];
        }
    }

    private void Check(string name, bool pass)
    {
        assertions++;
        if (!pass)
        {
            failures++;
        }
        report.AppendLine((pass ? "PASS " : "FAIL ") + name);
    }

    private void Fail(string name)
    {
        Check(name, false);
        Finish();
    }

    private void Finish()
    {
        if (finished)
        {
            return;
        }
        finished = true;
        phase = Phase.Done;
        RestorePlayerHp(1, savedHp1);
        RestorePlayerHp(2, savedHp2);
        if (growthSamples > 0 && growthFirstMem > 0 && growthLastMem > 0)
        {
            double memGrowth = (growthLastMem - growthFirstMem) * 100.0 / growthFirstMem;
            report.AppendLine("growth summary: samples=" + growthSamples
                + " mem " + growthFirstMem + " -> " + growthLastMem + " (" + memGrowth.ToString("F1") + "%)"
                + " enemies " + growthFirstEnemies + " -> " + growthLastEnemies
                + " clones " + growthFirstClones + " -> " + growthLastClones
                + " sceneSwitches=" + growthSceneSwitches
                + " zonesOpened=" + growthZonesOpened);
            Check("the long session kept memory growth under 20%", memGrowth < 20.0);
            Check("no runaway entity growth over the long session",
                growthLastEnemies <= Math.Max(1, growthFirstEnemies) * 2 + 2
                && growthLastClones <= Math.Max(1, growthFirstClones) * 2 + 4);
        }
        report.AppendLine("RESULT assertions=" + assertions + " failures=" + failures);
        try
        {
            Type context = FindType("IceyCoop.GameBridge.BridgeContext");
            MethodInfo snapshot = context == null ? null : context.GetMethod("ShutdownSnapshot", StaticFlags);
            if (snapshot != null)
            {
                report.AppendLine("bridge: " + snapshot.Invoke(null, null));
            }
        }
        catch
        {
        }
        try
        {
            File.WriteAllText(output, report.ToString());
        }
        catch
        {
        }

    }

    private static string F(float value)
    {
        return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Type FindType(string name)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type type = assemblies[i].GetType(name, false);
            if (type != null)
            {
                return type;
            }
        }
        return null;
    }

    private static object Read(object instance, string name)
    {
        if (instance == null)
        {
            return null;
        }
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return field.GetValue(instance);
            }
            PropertyInfo property = type.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property != null)
            {
                return property.GetValue(instance, null);
            }
        }
        return null;
    }

    private static object ReadStaticMember(Type type, string name)
    {
        if (type == null)
        {
            return null;
        }
        FieldInfo field = type.GetField(name, StaticFlags);
        if (field != null)
        {
            return field.GetValue(null);
        }
        PropertyInfo property = type.GetProperty(name, StaticFlags);
        return property == null ? null : property.GetValue(null, null);
    }

    private static void Write(object instance, string name, object value)
    {
        if (instance == null)
        {
            throw new ArgumentNullException("instance");
        }
        for (Type type = instance.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }
            PropertyInfo property = type.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value, null);
                return;
            }
        }
        throw new MissingMemberException(instance.GetType().FullName, name);
    }
}
