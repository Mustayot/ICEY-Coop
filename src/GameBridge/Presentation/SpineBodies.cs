using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Resolves the SkeletonAnimation a SpineAnimationController actually drives.
    ///
    /// An enemy prefab carries several skeletons (body, weakEffect, QTE, weapon parts);
    /// the visible body is the one the controller holds in its private
    /// _skeletonAnimation field, which SpineAnimationController.Awake fills with
    /// GetComponent&lt;SkeletonAnimation&gt;(). "First SkeletonAnimation in the children"
    /// is NOT a safe substitute: for DaoBrother / Bee the controller sits elsewhere in
    /// the hierarchy, so that guess lands on an auxiliary skeleton, the host reported
    /// the auxiliary clip (literally named Null) and the client played clips onto art
    /// nobody looks at.
    ///
    /// Field-first, component fallback. Reading the field once per controller and
    /// caching keeps the per-snapshot cost at a dictionary hit.
    /// </summary>
    public static class SpineBodies
    {
        private static readonly FieldInfo skeletonField = typeof(SpineAnimationController).GetField(
            "_skeletonAnimation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly Dictionary<SpineAnimationController, SkeletonAnimation> cache =
            new Dictionary<SpineAnimationController, SkeletonAnimation>();

        private static int fieldDiag;

        /// <summary>Auxiliary skeletons keep a clip literally named Null (or None/Empty)
        /// on track 0. Reporting or playing those blanks the body art, so both ends
        /// filter them.</summary>
        public static bool IsPlaceholderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }
            string trimmed = name.Trim();
            return string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Empty", StringComparison.OrdinalIgnoreCase);
        }

        public static SkeletonAnimation BodyOf(SpineAnimationController controller)
        {
            if (controller == null)
            {
                return null;
            }
            SkeletonAnimation cached;
            if (cache.TryGetValue(controller, out cached))
            {
                if (cached != null)
                {
                    return cached;
                }
                cache.Remove(controller);
            }
            SkeletonAnimation body = null;
            if (skeletonField != null)
            {
                try
                {
                    body = skeletonField.GetValue(controller) as SkeletonAnimation;
                }
                catch (Exception ex)
                {
                    if (fieldDiag < 4)
                    {
                        fieldDiag++;
                        BridgeContext.Info("[t17] spine body field read failed: " + ex.Message);
                    }
                }
            }
            else if (fieldDiag < 4)
            {
                fieldDiag++;
                BridgeContext.Info("[t17] spine body field missing; falling back to component lookup");
            }
            if (body == null)
            {
                body = controller.GetComponent<SkeletonAnimation>();
            }
            if (body == null)
            {
                body = controller.GetComponentInChildren<SkeletonAnimation>(true);
            }
            if (body != null)
            {
                cache[controller] = body;
            }
            return body;
        }
    }
}
