using System;
using IceyCoop.Core;
using IceyCoop.GameBridge.Combat;
using IceyCoop.GameBridge.Network;
using IceyCoop.GameBridge.Presentation;
using IceyCoop.GameBridge.Progression;
using IceyCoop.GameBridge.Story;
using IceyCoop.GameBridge.World;

namespace IceyCoop.GameBridge.Lobby
{
    /// <summary>
    /// Clears co-op-only process state after the native game has reached ui_start.
    /// Caller order: stop the old SessionOrchestrator, uninstall its PatchInstaller,
    /// call AfterTitleEntered(), then choose the next role and install its patch set.
    /// Never call while gameplay is being left for another gameplay scene.
    /// </summary>
    public static class LobbyRuntimeCleanup
    {
        public static void AfterTitleEntered()
        {
            string sceneName;
            try
            {
                sceneName = LevelManager.SceneName;
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[lobby] title cleanup skipped; scene unavailable: " + ex.Message);
                return;
            }
            if (!string.Equals(sceneName, "ui_start", StringComparison.Ordinal))
            {
                BridgeContext.Error("[lobby] title cleanup refused outside ui_start; scene=" + sceneName);
                return;
            }
            if (!BridgeContext.IsOffline)
            {
                BridgeContext.Error("[lobby] title cleanup requires SessionOrchestrator.Shutdown first");
                return;
            }

            // Remote slots carry axes, edges and held-charge cancellation state.
            InputAdapter.ResetRemoteInput(PlayerIds.P1);
            InputAdapter.ResetRemoteInput(PlayerIds.P2);

            // Restore the local P1 camera while its root is still available, then
            // remove all known player presentation ids and the active enemy set.
            CameraHudAdapter.RestoreLocalFollow();
            ReplicaFactory.RemovePlayer(CombatRouter.EntityIdP1);
            ReplicaFactory.RemovePlayer(CombatRouter.EntityIdP2);
            ulong[] enemyIds = ReplicaFactory.ActiveEnemyIds();
            for (int i = 0; i < enemyIds.Length; i++)
            {
                ReplicaFactory.RemoveEnemy(enemyIds[i]);
            }

            // P2Root may be either the client's replica or the host's persistent
            // clone. The bridge releases only those designated P2 roots and keeps
            // P1's native root and registry entries intact.
            BridgeContext.ReleaseSecondPlayerForLobby();

            InteractionBridge.ResetRuntimeState();
            PartyAssemblyGate.PendingScene = string.Empty;
            SceneLoadGate.Authorized = false;

            // These are captured wire-snapshot flags only. Do not call native
            // story APIs or clear ControlLockState: there is no safe public reset
            // for that readonly value, and a native single-player story lock must
            // remain under the game's control.
            StoryCapture.WorldTimePaused = false;
            StoryCapture.LastStoryFlag = null;
            StoryCapture.GameDataSaved = false;
            StoryCapture.BattleZoneAppeared = false;

            // Title/offline mode must use vanilla persistence. The next coop role
            // arms the guard only after its patch set has installed successfully.
            NativeSaveGuard.Disarm();
            BridgeContext.Info("[lobby] title runtime cleanup completed");
        }
    }
}
