using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Recovers host P2 only for scene entry or an evidenced unsafe position.
    /// Within one scene the policy uses P2's own grounded safe point; active
    /// PlayerRange bounds remain authoritative for boss arenas and narrow rooms.
    /// </summary>
    public static class P2Anchor
    {
        private const string DisableEnv = "ICEYCOOP_NO_P2_ANCHOR";
        private const float FallVelocityGate = -2f;
        private const float MapExitMargin = 0.25f;
        private const float BuriedSnapDelaySeconds = 1.25f;
        private const float BodyClearanceScale = 0.82f;
        private const float AnchorProbeHeight = 0.2f;
        private const float SideOffset = 2f;
        private const float RangeTolerance = 0.05f;
        private const float DiagnosticIntervalSeconds = 2f;

        private static readonly bool disabled = Environment.GetEnvironmentVariable(DisableEnv) == "1";
        private static readonly Collider2D[] overlapScratch = new Collider2D[16];
        private static readonly P2RecoveryPolicy recovery = new P2RecoveryPolicy();

        private static int sceneLoadGeneration;
        private static float buriedSince = -1f;
        private static string lastDiagnosticKey;
        private static float lastDiagnosticAt = -100f;

        static P2Anchor()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>How many times P2 has been re-placed (trace/verification aid).</summary>
        public static int Resnaps;

        public static void Tick()
        {
            if (disabled || !BridgeContext.IsHost)
            {
                return;
            }

            GameObject p1 = BridgeContext.P1Root;
            GameObject p2 = BridgeContext.P2Root;
            if (p1 == null || p2 == null)
            {
                return;
            }

            string sceneName;
            try
            {
                sceneName = LevelManager.SceneName;
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            string sceneKey = sceneName + "#" + activeScene.buildIndex.ToString() +
                "#" + sceneLoadGeneration.ToString();
            bool sceneChanged = recovery.BeginScene(sceneKey);
            if (sceneChanged)
            {
                buriedSince = -1f;
            }

            Vector3 p1Position = p1.transform.position;
            Vector3 p2Position = p2.transform.position;
            Rigidbody2D body = p2.GetComponent<Rigidbody2D>();
            Collider2D actorCollider = p2.GetComponent<Collider2D>();
            PlayerAttribute attribute = BridgeContext.EnsureBinding(PlayerIds.P2).Attribute;
            Rect playerRange = GameArea.PlayerRange;
            Rect mapRange = GameArea.MapRange;
            bool rangesValid = IsValidRange(playerRange) && IsValidRange(mapRange);

            bool currentInsideRange = rangesValid && IsInsideRange(playerRange, p2Position, actorCollider);
            bool currentBodyClear = actorCollider != null && !HasStaticBodyOverlap(p2, p2Position, actorCollider);
            bool grounded = attribute != null && attribute.isOnGround;
            if (!sceneChanged && !recovery.SceneEntryPending && grounded &&
                currentInsideRange && currentBodyClear)
            {
                recovery.RecordSafePosition(sceneKey, p2Position.x, p2Position.y,
                    true, true, true);
            }

            bool outsideMap = rangesValid && IsOutsideMap(mapRange, p2Position, actorCollider, body);
            bool currentlyBuried = actorCollider != null && !currentBodyClear;
            if (currentlyBuried)
            {
                if (buriedSince < 0f)
                {
                    buriedSince = Time.realtimeSinceStartup;
                }
            }
            else
            {
                buriedSince = -1f;
            }
            bool deeplyBuried = currentlyBuried &&
                Time.realtimeSinceStartup - buriedSince >= BuriedSnapDelaySeconds;

            P2RecoveryPosition safePosition;
            bool safePositionUsable = recovery.TryGetSafePosition(sceneKey, out safePosition) &&
                rangesValid && IsInsideRange(playerRange,
                    new Vector3(safePosition.X, safePosition.Y, p2Position.z), actorCollider) &&
                actorCollider != null && !HasStaticBodyOverlap(p2,
                    new Vector3(safePosition.X, safePosition.Y, p2Position.z), actorCollider);

            Vector3 sceneAnchor = Vector3.zero;
            bool sceneAnchorAvailable = recovery.SceneEntryPending && rangesValid &&
                TryChooseSceneAnchor(p1Position, p2, actorCollider, playerRange, out sceneAnchor);

            P2RecoveryDecision decision = recovery.Decide(outsideMap, deeplyBuried,
                safePositionUsable, sceneAnchorAvailable,
                new P2RecoveryPosition(sceneAnchor.x, sceneAnchor.y));

            if (decision.Action == P2RecoveryAction.None)
            {
                if (recovery.SceneEntryPending)
                {
                    LogHeld(sceneKey, "scene-entry-no-clear-point", p1Position, p2Position);
                }
                else if (outsideMap || deeplyBuried)
                {
                    LogHeld(sceneKey, safePositionUsable ? "recovery-not-selected" : "unsafe-no-current-safe-point",
                        p1Position, p2Position);
                }
                return;
            }

            string reason = decision.Action == P2RecoveryAction.EnterSceneNearPartner
                ? "scene-entry" : (outsideMap ? "outside-map" : "deeply-buried");
            Vector3 destination = new Vector3(decision.Position.X, decision.Position.Y, p2Position.z);
            p2.transform.position = destination;
            if (body != null)
            {
                body.velocity = Vector2.zero;
                body.angularVelocity = 0f;
            }
            if (decision.Action == P2RecoveryAction.EnterSceneNearPartner)
            {
                recovery.CompleteSceneEntry();
            }

            Resnaps++;
            buriedSince = -1f;
            lastDiagnosticKey = null;
            BridgeContext.Info(string.Format(
                "[actor] P2 recovery reason={0} scene={1} sceneKey={2} from=({3:F2},{4:F2}) to=({5:F2},{6:F2}) grounded={7} inRange={8} n={9}",
                reason, sceneName, sceneKey, p2Position.x, p2Position.y,
                destination.x, destination.y, grounded, currentInsideRange, Resnaps));
        }

        private static bool IsValidRange(Rect range)
        {
            return range.width > 0.01f && range.height > 0.01f;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            sceneLoadGeneration++;
        }

        private static bool IsInsideRange(Rect range, Vector3 rootPosition, Collider2D actorCollider)
        {
            if (actorCollider == null || !IsValidRange(range))
            {
                return false;
            }
            Vector3 centerOffset = actorCollider.bounds.center - actorCollider.transform.position;
            Vector2 half = actorCollider.bounds.extents;
            float centerX = rootPosition.x + centerOffset.x;
            float centerY = rootPosition.y + centerOffset.y;
            return centerX - half.x >= range.xMin - RangeTolerance &&
                centerX + half.x <= range.xMax + RangeTolerance &&
                centerY - half.y >= range.yMin - RangeTolerance &&
                centerY + half.y <= range.yMax + RangeTolerance;
        }

        private static bool IsOutsideMap(Rect mapRange, Vector3 position, Collider2D actorCollider, Rigidbody2D body)
        {
            if (actorCollider == null || body == null)
            {
                return false;
            }

            Vector3 centerOffset = actorCollider.bounds.center - actorCollider.transform.position;
            Vector2 half = actorCollider.bounds.extents;
            float centerY = position.y + centerOffset.y;
            bool fallingBelowMap = centerY + half.y < mapRange.yMin - MapExitMargin &&
                body.velocity.y < FallVelocityGate;
            // Horizontal edges are already owned by native MoveAreaLimit/PlayerRange.
            // A high upward position can be valid gameplay, so neither is a recovery signal.
            return fallingBelowMap;
        }

        private static bool TryChooseSceneAnchor(Vector3 p1Position, GameObject p2,
            Collider2D actorCollider, Rect playerRange, out Vector3 anchor)
        {
            float[] offsets = { SideOffset, -SideOffset, 1f, -1f, 0f };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 candidate = new Vector3(
                    p1Position.x + offsets[i], p1Position.y + AnchorProbeHeight, p1Position.z);
                if (IsInsideRange(playerRange, candidate, actorCollider) &&
                    !HasStaticBodyOverlap(p2, candidate, actorCollider))
                {
                    anchor = candidate;
                    return true;
                }
            }

            anchor = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Checks the inset centre of P2's actual collider. The inset avoids treating
        /// ordinary wall contact or floor contact as penetration; only static overlap
        /// through the body's interior blocks recording or using a point.
        /// </summary>
        private static bool HasStaticBodyOverlap(GameObject p2, Vector3 rootPosition, Collider2D actorCollider)
        {
            if (p2 == null || actorCollider == null)
            {
                return true;
            }

            Vector3 currentRoot = actorCollider.transform.position;
            Vector3 centerOffset = actorCollider.bounds.center - currentRoot;
            Vector3 boundsSize = actorCollider.bounds.size;
            Vector2 center = new Vector2(rootPosition.x + centerOffset.x, rootPosition.y + centerOffset.y);
            Vector2 insetSize = new Vector2(
                Mathf.Max(0.05f, boundsSize.x * BodyClearanceScale),
                Mathf.Max(0.05f, boundsSize.y * BodyClearanceScale));
            int count = Physics2D.OverlapBoxNonAlloc(center, insetSize, 0f, overlapScratch);
            if (count >= overlapScratch.Length)
            {
                // A saturated result buffer cannot prove the landing is clear.
                return true;
            }
            for (int i = 0; i < count; i++)
            {
                Collider2D other = overlapScratch[i];
                if (other == null || other.isTrigger || IsOwnOrDynamic(other, p2))
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        private static bool IsOwnOrDynamic(Collider2D other, GameObject p2)
        {
            if (p2 != null && (other.gameObject == p2 || other.transform.IsChildOf(p2.transform)))
            {
                return true;
            }
            if (other.attachedRigidbody != null && !other.attachedRigidbody.isKinematic)
            {
                return true;
            }
            if (other.GetComponent<EnemyAttribute>() != null || other.GetComponent<PlayerAttribute>() != null)
            {
                return true;
            }
            return other.CompareTag("Player") || other.CompareTag("Enemy");
        }

        private static void LogHeld(string sceneKey, string reason, Vector3 p1Position, Vector3 p2Position)
        {
            string key = sceneKey + ":" + reason;
            float now = Time.realtimeSinceStartup;
            if (string.Equals(lastDiagnosticKey, key, StringComparison.Ordinal) &&
                now - lastDiagnosticAt < DiagnosticIntervalSeconds)
            {
                return;
            }
            lastDiagnosticKey = key;
            lastDiagnosticAt = now;
            P2RecoveryPosition safe;
            bool hasSafe = recovery.TryGetSafePosition(sceneKey, out safe);
            BridgeContext.Info(string.Format(
                "[actor] P2 recovery held reason={0} sceneKey={1} p1=({2:F2},{3:F2}) p2=({4:F2},{5:F2}) safeScene={6} safe=({7:F2},{8:F2})",
                reason, sceneKey, p1Position.x, p1Position.y, p2Position.x, p2Position.y,
                hasSafe ? sceneKey : "<none>", hasSafe ? safe.X : 0f, hasSafe ? safe.Y : 0f));
        }
    }
}
