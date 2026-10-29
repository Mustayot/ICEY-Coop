using System;
using HarmonyLib;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.World
{
    /// <summary>
    /// T19: the host owns scene changes.
    ///
    /// Vanilla still tries to change scenes on its own on the client (story triggers, gates,
    /// the player walking out of a room). If that happens while a session is up, the client
    /// loads a room the host never agreed to and the two worlds drift apart. This gate drops
    /// those loads and lets only two things through:
    ///   - the authorised auto-load issued by SessionOrchestrator when the host sends
    ///     ScenePrepare (Authorized flag around the call),
    ///   - anything that is not a gameplay scene (splash / ui_start / menu), so boot and the
    ///     title screen keep working.
    /// </summary>
    public static class SceneLoadGate
    {
        private const string DisableEnv = "ICEYCOOP_ALLOW_CLIENT_SCENE_LOADS";

        private static readonly bool disabled = Environment.GetEnvironmentVariable(DisableEnv) == "1";

        /// <summary>Set by the session while it issues an approved load.</summary>
        public static bool Authorized;

        /// <summary>How many native scene loads were refused (verification aid).</summary>
        public static int Blocked;

        private static int logged;
        /// <summary>Issues an approved load (used by the session for ScenePrepare and retries).</summary>
        public static void LoadAuthorized(string sceneName)
        {
            Authorized = true;
            try
            {
                LevelManager.LoadScene(sceneName);
            }
            finally
            {
                Authorized = false;
            }
        }

        public static bool PrefixLoadScene(string __0)
        {
            if (disabled || Authorized)
            {
                return true;
            }
            // T19 assembly: on the host a room change must not strand the partner. The gate holds
            // the load until both standing players are together and then releases it itself
            // (ICEYCOOP_PARTY_GATE=1; see PartyAssemblyGate for why it is opt-in).
            if (BridgeContext.IsHost && !BridgeContext.IsClient
                && PartyAssemblyGate.Enabled && SceneWhitelist.LooksLikeGameplayScene(__0))
            {
                if (PartyAssemblyGate.Intercept(__0))
                {
                    return false;
                }
            }
            if (!BridgeContext.IsClient)
            {
                // T20: the host just committed a room change. If the partner is down they have to
                // be carried across and revived in the new room, not left behind as a corpse.
                if (SceneWhitelist.LooksLikeGameplayScene(__0))
                {
                    Progression.PartyRevive.NoteSceneCommitted(__0);
                }
                return true;
            }
            // Boot and menu scenes must load freely; only gameplay rooms are gated.
            if (!SceneWhitelist.LooksLikeGameplayScene(__0))
            {
                return true;
            }
            Blocked++;
            if (logged < 5)
            {
                logged++;
                BridgeContext.Info(string.Format(
                    "[t19] refused unrequested client scene load scene={0} (host owns transitions; ICEYCOOP_ALLOW_CLIENT_SCENE_LOADS=1 overrides)",
                    __0));
            }
            return false;
        }
    }
}
