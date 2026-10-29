using System.Reflection;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// T16 HUD/camera adapter. Routes the HUD playerAttribute read and the camera
    /// follow target to the local slot: P2 on the client, P1 on host/offline.
    /// </summary>
    public static class CameraHudAdapter
    {
        /// <summary>
        /// Set while a host camera cue is being replayed on the client. The native
        /// CameraZoom tween needs the pivot to stay where the cue put it; the
        /// per-frame follow enforcement is suspended until ZoomFinished fires and
        /// RestoreLocalFollow clears this flag.
        /// </summary>
        public static bool ReplayingZoom;

        public static PlayerAttribute GetLocalPlayerAttribute()
        {
            if (BridgeContext.IsClient)
            {
                NativePlayerBinding binding = BridgeContext.EnsureBinding(PlayerIds.P2);
                if (binding.Attribute != null)
                {
                    return binding.Attribute;
                }
                return R.Player.Attribute;
            }
            return R.Player.Attribute;
        }
        private static GameObject followAnchor;
        private static Vector3 followOffset = new Vector3(0f, 1.1f, 0f);
        private static bool offsetCaptured;

        /// <summary>Anchor transform under the local slot root, placed at the native pivot height.</summary>
        private static Transform EnsureAnchor(Transform root)
        {
            if (root == null) return null;
            if (followAnchor == null) followAnchor = new GameObject("coop-camera-anchor");
            if (followAnchor.transform.parent != root) followAnchor.transform.SetParent(root, false);
            followAnchor.transform.localPosition = followOffset;
            return followAnchor.transform;
        }

        public static byte LocalSlot
        {
            get { return BridgeContext.IsClient ? PlayerIds.P2 : PlayerIds.P1; }
        }

        /// <summary>Restores the camera pivot to the local slot root after a
        /// native zoom sequence pinned it back to R.Player.</summary>
        public static void RestoreLocalFollow()
        {
            ReplayingZoom = false;
            GameObject root = BridgeContext.IsClient ? BridgeContext.P2Root : BridgeContext.P1Root;
            if (root == null)
            {
                root = BridgeContext.P1Root;
            }
            if (root == null)
            {
                return;
            }

            CameraController controller = Object.FindObjectOfType<CameraController>();
            if (controller != null)
            {
                // The native LateUpdate only follows _pivot when IsFollowPivot is
                // true, and nothing sets it true on the client (the vanilla init
                // path never runs for the mirrored slot). Without this the camera
                // stays pinned wherever it spawned instead of following the replica.
                controller.IsFollowPivot = true;
                FieldInfo pivotField = typeof(CameraController).GetField("_pivot",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pivotField != null)
                {
                    // The native pivot is not the player root: it sits above the feet.
                    // Pointing it straight at our replica root dropped the camera by that
                    // offset, which pushed the level up and showed empty space below.
                    // Measure the native offset once and keep following the replica from
                    // the same height.
                    Transform nativePivot = pivotField.GetValue(controller) as Transform;
                    if (!offsetCaptured)
                    {
                        offsetCaptured = true;
                        if (nativePivot != null && R.Player != null && R.Player.transform != null)
                        {
                            Vector3 delta = nativePivot.position - R.Player.transform.position;
                            if (Mathf.Abs(delta.x) < 6f && Mathf.Abs(delta.y) < 6f && Mathf.Abs(delta.z) < 6f)
                            {
                                followOffset = delta;
                            }
                        }
                        string envY = System.Environment.GetEnvironmentVariable("ICEYCOOP_CAMERA_OFFSET_Y");
                        float parsed;
                        if (!string.IsNullOrEmpty(envY) && float.TryParse(envY, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                        {
                            followOffset = new Vector3(followOffset.x, parsed, followOffset.z);
                        }
                        BridgeContext.Info(string.Format("[t16] camera follow offset captured native=({0:F2},{1:F2}) applied=({2:F2},{3:F2})",
                            nativePivot == null ? 0f : nativePivot.position.x, nativePivot == null ? 0f : nativePivot.position.y,
                            followOffset.x, followOffset.y));
                    }
                    pivotField.SetValue(controller, EnsureAnchor(root.transform));
                }
            }
            BridgeContext.Info(string.Format("[t16] zoom finished, follow restored slot={0}", LocalSlot));
        }

        /// <summary>Per-frame hook reserved for interpolation ticks; currently the
        /// presenters interpolate inside ApplySnapshot, so this is a no-op.</summary>
        private static int enforceLogs;

        /// <summary>
        /// Per-frame safety net: the native zoom sequence is the only place that used to
        /// re-point the pivot, and in practice it does not fire during normal play, so the
        /// client camera kept following the leftover native player. Enforce the local slot
        /// anchor here, and capture the native follow height the first time we see the
        /// native pivot still in charge.
        /// </summary>
        public static void Update()
        {
            if (!BridgeContext.IsClient) return;
            if (ReplayingZoom) return;
            GameObject root = BridgeContext.P2Root;
            if (root == null) return;
            CameraController controller = Object.FindObjectOfType<CameraController>();
            if (controller == null) return;
            controller.IsFollowPivot = true;
            FieldInfo pivotField = typeof(CameraController).GetField("_pivot",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pivotField == null) return;
            Transform current = pivotField.GetValue(controller) as Transform;
            Transform want = EnsureAnchor(root.transform);
            if (want == null || current == want) return;
            if (!offsetCaptured)
            {
                offsetCaptured = true;
                if (current != null && R.Player != null && R.Player.transform != null)
                {
                    Vector3 delta = current.position - R.Player.transform.position;
                    if (Mathf.Abs(delta.x) < 6f && Mathf.Abs(delta.y) < 6f && Mathf.Abs(delta.z) < 6f)
                    {
                        followOffset = delta;
                    }
                }
                string envY = System.Environment.GetEnvironmentVariable("ICEYCOOP_CAMERA_OFFSET_Y");
                float parsed;
                if (!string.IsNullOrEmpty(envY) && float.TryParse(envY, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    followOffset = new Vector3(followOffset.x, parsed, followOffset.z);
                }
                BridgeContext.Info(string.Format("[t16] camera follow offset captured nativePivot={0} applied=({1:F2},{2:F2})",
                    current == null ? "<null>" : current.name, followOffset.x, followOffset.y));
            }
            want.localPosition = followOffset;
            pivotField.SetValue(controller, want);
            if (enforceLogs < 5)
            {
                enforceLogs++;
                BridgeContext.Info(string.Format("[t16] camera follow enforced slot={0} from={1}",
                    LocalSlot, current == null ? "<null>" : current.name));
            }
        }
    }
}
