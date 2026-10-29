using System;
using System.Reflection;
using HarmonyLib;
using IceyCoop.GameBridge.Combat;
using IceyCoop.Protocol;
using UnityEngine;

namespace IceyCoop.GameBridge.Story
{
    /// <summary>
    /// G7 camera-cue capture. Story sequences drive the camera through two generic
    /// native seams: CameraController.CameraZoom (focus/zoom) and
    /// CameraController.CameraShake (shake). The host records the exact call and
    /// emits a CameraCue=11 event; the client replays it on its local controller
    /// so a mid-sequence joiner sees the same camera work. Host only: Emit is a
    /// no-op unless EntityAdapter.HostEventSink is wired.
    /// </summary>
    public static class CameraCueCapture
    {
        private static int zoomPatched;
        private static int shakePatched;

        public static int PatchedCount
        {
            get { return zoomPatched + shakePatched; }
        }

        public static void Install(Harmony harmony)
        {
            Type controller = AccessTools.TypeByName("CameraController");
            if (controller == null)
            {
                BridgeContext.Info("[g7] camera cue capture installed: CameraController not found");
                return;
            }

            MethodInfo zoom = AccessTools.Method(controller, "CameraZoom");
            if (zoom != null)
            {
                MethodInfo postfix = typeof(CameraCueCapture).GetMethod("PostfixZoom", BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(zoom, null, new HarmonyMethod(postfix));
                zoomPatched = 1;
            }

            MethodInfo shake = AccessTools.Method(controller, "CameraShake");
            if (shake != null)
            {
                MethodInfo postfix = typeof(CameraCueCapture).GetMethod("PostfixShake", BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(shake, null, new HarmonyMethod(postfix));
                shakePatched = 1;
            }

            BridgeContext.Info(string.Format("[g7] camera cue capture installed: zoom={0} shake={1}", zoomPatched, shakePatched));
        }

        /// <summary>CameraZoom(Vector3 pos, float second, float deltaZ=3).</summary>
        public static void PostfixZoom(object[] __args)
        {
            if (__args == null || __args.Length < 2)
            {
                return;
            }
            CameraCuePayload p = new CameraCuePayload();
            p.Action = CameraCueCodec.ActionZoom;
            try
            {
                Vector3 pos = (Vector3)__args[0];
                p.A = pos.x;
                p.B = pos.y;
                p.C = pos.z;
            }
            catch (InvalidCastException)
            {
                return; // never ship a cue we could not read faithfully
            }
            p.D = Convert.ToSingle(__args[1]);
            p.E = __args.Length > 2 ? Convert.ToSingle(__args[2]) : 3f;
            Emit(p);
        }

        /// <summary>CameraShake(float second, float strength=0.2, ShakeTypeEnum type=Rect, bool isLoop=false).</summary>
        public static void PostfixShake(object[] __args)
        {
            if (__args == null || __args.Length < 1)
            {
                return;
            }
            CameraCuePayload p = new CameraCuePayload();
            p.Action = CameraCueCodec.ActionShake;
            p.A = Convert.ToSingle(__args[0]);
            p.B = __args.Length > 1 ? Convert.ToSingle(__args[1]) : 0.2f;
            int type = __args.Length > 2 ? Convert.ToInt32(__args[2]) : 2;
            p.X = (byte)(type < 0 || type > CameraCueCodec.MaxShakeType ? 2 : type);
            bool loop = __args.Length > 3 && __args[3] != null && (bool)__args[3];
            p.Y = loop ? (byte)1 : (byte)0;
            Emit(p);
        }

        private static void Emit(CameraCuePayload p)
        {
            if (EntityAdapter.HostEventSink == null)
            {
                return;
            }
            EntityAdapter.HostEventSink((int)WorldEventSubType.CameraCue, 0, 0, CameraCueCodec.Encode(p));
            BridgeContext.Info("[g7] camera cue emitted action=" + p.Action);
        }
    }
}
