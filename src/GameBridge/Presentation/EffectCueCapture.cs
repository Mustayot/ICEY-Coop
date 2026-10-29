using System;
using System.Reflection;
using HarmonyLib;
using IceyCoop.GameBridge.Combat;
using IceyCoop.Protocol;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Host-side effect capture. Every dynamically spawned visual in the game - sword
    /// trails, charge glow, execution bursts, hit sparks, enemy attacks and explosions -
    /// goes through the single native choke point
    /// EffectController.Generate(int effectId, Transform target, Vector3 position,
    /// Vector3 rotation, Vector3 scale, bool useFxZNum), which pulls from an object pool.
    ///
    /// The host records each call and emits an EffectCue=17 event; the client replays it
    /// through its own EffectController at the mirrored transform. That is the whole
    /// reason the client used to show no effects at all: its player is a puppet and its
    /// enemies never ran the native ability code that calls Generate.
    ///
    /// Host only: Emit is a no-op unless EntityAdapter.HostEventSink is wired.
    /// </summary>
    public static class EffectCueCapture
    {
        private static int generatePatched;
        private static int unresolvedPathLogs;

        public static int PatchedCount
        {
            get { return generatePatched; }
        }

        /// <summary>Cues emitted since start (diagnostics).</summary>
        public static int EmittedCount;

        /// <summary>Per second budget for emitted cues; 0 = unlimited.
        /// A busy fight is expected to stay well under 100/s.</summary>
        private static readonly int maxPerSecond = ReadBudget();

        private static float windowStart;
        private static int windowCount;
        private static int droppedThisWindow;
        private static int lastReportedSecond = -1;

        private static int ReadBudget()
        {
            string raw = Environment.GetEnvironmentVariable("ICEYCOOP_FXCUE_MAX_PER_SEC");
            int value;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out value) && value > 0)
            {
                return value;
            }
            return 0;
        }

        public static void Install(Harmony harmony)
        {
            MethodInfo generate = AccessTools.Method(typeof(EffectController), "Generate");
            if (generate == null)
            {
                BridgeContext.Info("[fx] effect cue capture installed: EffectController.Generate not found");
                return;
            }
            MethodInfo postfix = typeof(EffectCueCapture).GetMethod("PostfixGenerate",
                BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(generate, null, new HarmonyMethod(postfix));
            generatePatched = 1;
            BridgeContext.Info(string.Format("[fx] effect cue capture installed: generate=1 budget={0}",
                maxPerSecond == 0 ? "unlimited" : maxPerSecond + "/s"));
        }

        /// <summary>Postfix on EffectController.Generate. Reads each argument defensively:
        /// a cue we cannot read faithfully is dropped rather than shipped wrong.</summary>
        public static void PostfixGenerate(object[] __args, Transform __result)
        {
            if (EntityAdapter.HostEventSink == null || __args == null || __args.Length < 6)
            {
                return;
            }
            EffectCuePayload p = new EffectCuePayload();
            try
            {
                p.EffectId = Convert.ToInt32(__args[0]);
                Transform target = __args[1] as Transform;
                Vector3 position = (Vector3)__args[2];
                Vector3 rotation = (Vector3)__args[3];
                Vector3 scale = (Vector3)__args[4];
                // Native semantics (EffectController.Generate IL): with a target, the
                // position argument is target-relative and the effect lands at
                // target.position + position; with no target it is already a world
                // position. Sending a world position for a parented call would place the
                // client's copy at origin + offset, which is metres off the character.
                ulong targetId = target == null ? 0UL : EntityAdapter.EntityIdFor(target.gameObject);
                if (target != null && targetId != 0)
                {
                    EffectTargetStep[] path;
                    if (!EffectTargetPath.TryCapture(target, targetId, out path))
                    {
                        if (unresolvedPathLogs++ < 8)
                            BridgeContext.Info("[fx] cue skipped: cannot preserve exact target path entity=" + targetId);
                        return;
                    }
                    p.AnchorMode = EffectCueCodec.AnchorTarget;
                    p.TargetEntityId = targetId;
                    p.TargetPath = path;
                    p.X = position.x;
                    p.Y = position.y;
                    p.Z = position.z;
                }
                else
                {
                    p.AnchorMode = EffectCueCodec.AnchorWorld;
                    p.TargetEntityId = 0;
                    // Net-zero difference for the ordinary (non-follow) case, exact for the
                    // follow rows: native keeps those parented, so a rotated/scaled target
                    // needs TransformPoint rather than a plain add.
                    Vector3 world = target == null
                        ? position
                        : (IsFollowEffect(p.EffectId) ? target.TransformPoint(position) : target.position + position);
                    p.X = world.x;
                    p.Y = world.y;
                    p.Z = world.z;
                }
                p.RotX = rotation.x;
                p.RotY = rotation.y;
                p.RotZ = rotation.z;
                p.ScaleX = scale.x;
                p.ScaleY = scale.y;
                p.ScaleZ = scale.z;
                p.UseFxZNum = __args[5] != null && (bool)__args[5] ? (byte)1 : (byte)0;
                // PlayerAnimEventListener.PlayEffect mirrors the spawned effect on X after
                // generating it. Capture the result's sign instead of guessing the rule, so
                // the client reproduces whatever the native caller did.
                if (__result != null)
                {
                    p.FlipX = __result.localScale.x < 0f ? (byte)1 : (byte)0;
                }
                if (EmittedCount < 24)
                {
                    BridgeContext.Info(string.Format("[fx] gen id={0} parent={1} targetId={2} mode={3} flip={4}",
                        p.EffectId, DescribeTarget(target), p.TargetEntityId, p.AnchorMode, p.FlipX));
                }
            }
            catch (InvalidCastException)
            {
                return;
            }
            catch (FormatException)
            {
                return;
            }
            Emit(p);
        }

        private static bool TakeBudget()
        {
            if (maxPerSecond <= 0)
            {
                return true;
            }
            float now = Time.realtimeSinceStartup;
            if (now - windowStart >= 1f)
            {
                if (droppedThisWindow > 0 && lastReportedSecond != (int)now)
                {
                    lastReportedSecond = (int)now;
                    BridgeContext.Info("[fx] effect cues throttled dropped=" + droppedThisWindow);
                }
                droppedThisWindow = 0;
                windowStart = now;
                windowCount = 0;
            }
            if (windowCount >= maxPerSecond)
            {
                droppedThisWindow++;
                return false;
            }
            windowCount++;
            return true;
        }

        private static void Emit(EffectCuePayload p)
        {
            if (!TakeBudget())
            {
                return;
            }
            byte[] payload;
            try
            {
                payload = EffectCueCodec.Encode(p);
            }
            catch (ProtocolException)
            {
                return;
            }
            EntityAdapter.HostEventSink((int)WorldEventSubType.EffectCue, 0, 0, payload);
            EmittedCount++;
            ReportRate();
            if (EmittedCount == 1 || EmittedCount % 200 == 0)
            {
                BridgeContext.Info(string.Format("[fx] effect cue emitted id={0} target={1} len={2} head={3} total={4}",
                    p.EffectId, p.TargetEntityId, payload.Length, HexHead(payload, 6), EmittedCount));
            }
        }

        /// <summary>Whether this effect row keeps its parent (EffectAttr.isFollow). Read from
        /// the same table the client has, so only the host needs it: it decides between
        /// TransformPoint and a plain add when the target cannot be identified.</summary>
        private static readonly System.Reflection.PropertyInfo fxDataProperty =
            typeof(EffectController).GetProperty("fxData");

        private static bool IsFollowEffect(int effectId)
        {
            try
            {
                EffectController fx = R.Effect;
                if (fx == null || fxDataProperty == null)
                {
                    return false;
                }
                System.Collections.IDictionary table = fxDataProperty.GetValue(fx, null) as System.Collections.IDictionary;
                if (table == null || !table.Contains(effectId))
                {
                    return false;
                }
                object attr = table[effectId];
                if (attr == null)
                {
                    return false;
                }
                System.Reflection.FieldInfo follow = attr.GetType().GetField("isFollow");
                return follow != null && (bool)follow.GetValue(attr);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Parent path for the first few cues: which object a native effect hangs
        /// off tells us whether the entity lookup can ever resolve it (a weapon bone under
        /// the player does; a standalone effect clone at the scene root does not).</summary>
        private static string DescribeTarget(Transform target)
        {
            if (target == null)
            {
                return "<null>";
            }
            Transform root = target;
            while (root.parent != null)
            {
                root = root.parent;
            }
            return target.name + "@" + root.name;
        }

        private static float rateWindowStart;
        private static int rateWindowCount;

        /// <summary>Five-second rate line so a session shows whether the capture is
        /// actually firing during play (a silent zero here means the client will see no
        /// effects at all, and the cause is upstream of the wire).</summary>
        private static void ReportRate()
        {
            float now = Time.realtimeSinceStartup;
            rateWindowCount++;
            if (rateWindowStart == 0f)
            {
                rateWindowStart = now;
                return;
            }
            if (now - rateWindowStart < 5f)
            {
                return;
            }
            BridgeContext.Info(string.Format("[fx] effect cues emitted last5s={0} total={1}",
                rateWindowCount, EmittedCount));
            rateWindowStart = now;
            rateWindowCount = 0;
        }

        /// <summary>First bytes of a payload, for wire diagnostics: a cue that decodes to
        /// a different id on the client than it had on the host means the bytes changed in
        /// transit (or the two sides disagree on the layout), and this pinpoints which.</summary>
        public static string HexHead(byte[] payload, int count)
        {
            if (payload == null)
            {
                return "<null>";
            }
            int n = payload.Length < count ? payload.Length : count;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(payload[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
