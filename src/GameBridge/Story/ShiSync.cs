using UnityEngine;

namespace IceyCoop.GameBridge.Story
{
    /// <summary>
    /// T27 SHI segment, plan A: host drives the SHI runner, the client watches.
    /// Registers the host's PlayerControllerDemo (the SHI platform runner) as a
    /// regular world entity so the existing snapshot/baseline pipeline ships its
    /// position to the client. The client hides its local runner (which would
    /// otherwise stand still and confuse the picture) and keeps a position-only
    /// mirror at the host coordinates.
    ///
    /// Plan C (two players each controlling a runner) is blocked on the native
    /// design: PlayerControllerDemo is a lazy singleton and reads Unity's global
    /// physical keyboard (Input.GetKeyUp(97/100)), so two runners would read the
    /// same keys. This file is the seam that plan C will extend later.
    /// </summary>
    public static class ShiSync
    {
        /// <summary>Entity kind for the SHI runner mirror (1=enemy, 2=player).</summary>
        public const int KindShi = 3;

        private static ulong lastId;
        private static GameObject lastGo;

        public static void Tick()
        {
            if (!BridgeContext.IsHost)
            {
                return;
            }
            string scene;
            try
            {
                scene = LevelManager.SceneName;
            }
            catch
            {
                return;
            }
            if (!string.Equals(scene, "SHI", System.StringComparison.Ordinal))
            {
                // Left the SHI segment: the runner GameObject was destroyed with the
                // scene. Despawn its entity so re-entering SHI starts a fresh one.
                if (lastId != 0 && lastGo == null)
                {
                    Combat.EntityAdapter.NotifyDespawnById(lastId);
                    lastId = 0;
                }
                return;
            }
            PlayerControllerDemo runner = PlayerControllerDemo.Instance;
            if (runner == null || runner.gameObject == null)
            {
                if (lastId != 0 && lastGo == null)
                {
                    Combat.EntityAdapter.NotifyDespawnById(lastId);
                    lastId = 0;
                }
                return;
            }
            lastGo = runner.gameObject;
            lastId = Combat.EntityAdapter.GetOrAssignId(runner.gameObject, KindShi, "shi");
        }
    }
}
