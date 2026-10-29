using System;
using UnityEngine;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Progression
{
    /// <summary>Explicit host startup transaction. Networking and save capture wait for Done.</summary>
    public static class HostStartupResume
    {
        private static readonly HostResumeGate gate = new HostResumeGate();
        private static CoopSaveSnapshot snapshot;
        private static float lastSceneRequestAt;
        private static int matchedFrames;
        public static int Restores { get; private set; }
        public static int Attempts { get { return gate.Attempts; } }
        public static string LastError { get { return gate.LastError; } }
        public static HostResumeState State { get { return gate.State; } }
        public static bool CanListen { get { return gate.CanListen; } }
        public static bool CanWriteSave { get { return gate.CanWriteSave; } }
        public static uint SourceEpoch { get { return snapshot == null ? 0u : snapshot.WorldEpoch; } }

        public static void Reset()
        {
            gate.Reset();
            snapshot = null;
            Restores = 0;
            matchedFrames = 0;
            lastSceneRequestAt = -10f;
        }

        public static void Begin(bool requested)
        {
            Reset();
            gate.Begin(requested, NowMs());
            if (!requested) return;
            if (!BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                Fail("host co-op authority required");
                return;
            }
            snapshot = CoopSaveWriter.Load(null);
            if (snapshot == null)
            {
                Fail("no readable co-op startup save (main or backup)");
                return;
            }
            string error;
            if (!snapshot.TryValidate(out error))
            {
                Fail("invalid startup save: " + error);
                return;
            }
            bool allowOverride = Environment.GetEnvironmentVariable(SaveCompatibility.OverrideEnv) == "1";
            if (!SaveCompatibility.IsAllowed(snapshot.Content, BuildIdentity.Current, allowOverride, out error))
            {
                Fail("startup save rejected: " + error);
                return;
            }
            if (snapshot.P1Hp <= 0 || snapshot.P2Hp <= 0)
            {
                Fail("saved player is downed; safe co-op rebirth restore is unavailable");
                return;
            }
            if (!SceneWhitelist.IsAllowed(Environment.GetEnvironmentVariable(SceneWhitelist.EnvList),
                Environment.GetEnvironmentVariable(SceneWhitelist.EnvStrict) == "1", snapshot.SceneName))
            {
                Fail("startup save scene is not permitted: " + snapshot.SceneName);
                return;
            }
            BridgeContext.Info("[resume-startup] pending scene=" + snapshot.SceneName
                + " sourceEpoch=" + snapshot.WorldEpoch + "; listener and save capture held");
        }

        public static void Tick()
        {
            if (gate.State != HostResumeState.Pending) return;
            if (!BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                Fail("host co-op authority lost during startup restore");
                return;
            }
            // Poll the deadline even while the native boot/scene/actor pipeline is incomplete.
            if (!gate.ShouldAttempt(NowMs())) return;
            try
            {
                string current = LevelManager.SceneName;
                if (!string.Equals(current, snapshot.SceneName, StringComparison.Ordinal))
                {
                    matchedFrames = 0;
                    // Splash can swallow native scene requests. Retry on a bounded cadence
                    // instead of issuing a new load every frame while the game boots.
                    if (Time.realtimeSinceStartup >= 3f
                        && Time.realtimeSinceStartup - lastSceneRequestAt >= 2.5f)
                    {
                        lastSceneRequestAt = Time.realtimeSinceStartup;
                        World.SceneLoadGate.LoadAuthorized(snapshot.SceneName);
                        BridgeContext.Info("[resume-startup] loading " + snapshot.SceneName);
                    }
                    return;
                }
                if (++matchedFrames < 3) return;
                string error;
                bool applied = CoopRestore.Apply(snapshot, out error);
                gate.NoteAttempt(applied, error, NowMs());
                if (gate.State == HostResumeState.Done)
                {
                    Restores++;
                    BridgeContext.Info("[resume-startup] ready; " + CoopRestore.LastResult
                        + "; new session owns its own network epoch");
                }
                else if (!applied && !string.IsNullOrEmpty(error)
                    && !error.StartsWith("retry:", StringComparison.Ordinal))
                {
                    Fail(error);
                }
            }
            catch (Exception ex)
            {
                Fail("startup restore failed: " + ex.Message);
            }
        }

        private static void Fail(string reason)
        {
            gate.Fail(reason);
            BridgeContext.Error("[resume-startup] failed: " + gate.LastError);
        }

        /// <summary>A different terminal session path must not leave a pending transaction alive.</summary>
        public static void CancelPending(string reason)
        {
            if (gate.State == HostResumeState.Pending)
                gate.Fail("session ended during startup restore: " + reason);
        }

        private static long NowMs() { return (long)(Time.realtimeSinceStartup * 1000f); }
    }
}
