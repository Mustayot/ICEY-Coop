using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>
    /// Join-scene gate.
    ///
    /// The old rule was a heuristic: any scene whose name looked like "C0",
    /// "C1" ... was accepted. That cannot tell a reviewed battle level from a
    /// menu or a story scene that writes world state, so a peer could be
    /// activated into a scene nobody had audited.
    ///
    /// A scene is now allowed only if it appears in an explicit whitelist.
    /// Strict mode refuses everything else; non-strict mode keeps the legacy
    /// heuristic for local development runs. Everything here is pure logic so
    /// the decision table can be unit tested without the game.
    /// </summary>
    public static class SceneWhitelist
    {
        /// <summary>Comma separated scene keys that have been reviewed.</summary>
        public const string EnvList = "ICEYCOOP_SCENE_WHITELIST";

        /// <summary>"1" turns the legacy heuristic off: only the list may join.</summary>
        public const string EnvStrict = "ICEYCOOP_STRICT_SCENE";

        /// <summary>Splits a comma separated whitelist, dropping blanks.</summary>
        public static string[] Parse(string csv)
        {
            if (csv == null)
            {
                return new string[0];
            }
            string[] raw = csv.Split(',');
            List<string> items = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                string item = raw[i].Trim();
                if (item.Length > 0)
                {
                    items.Add(item);
                }
            }
            return items.ToArray();
        }

        /// <summary>Ordinal-ignore-case membership test.</summary>
        public static bool IsListed(string csv, string scene)
        {
            if (scene == null)
            {
                return false;
            }
            string[] items = Parse(csv);
            for (int i = 0; i < items.Length; i++)
            {
                if (string.Equals(items[i], scene, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Legacy heuristic retained for non-strict local runs: a gameplay key
        /// starts with 'C' followed by a digit and is longer than two chars, so
        /// splash / ui_start never qualify.
        /// </summary>
        public static bool LooksLikeGameplayScene(string scene)
        {
            if (string.IsNullOrEmpty(scene))
            {
                return false;
            }
            return scene.Length > 2 && scene[0] == 'C' && char.IsDigit(scene[1]);
        }

        /// <summary>
        /// Special playable scenes outside the C-digit naming: SHI (the standalone SHI
        /// platform segment with its own Core.Input.Shi / PlayerControllerDemo /
        /// SHICameraController) and palace0-7 (the blood palace challenge mode).
        /// They are real gameplay, so the join gate must not reject them like menus.
        /// </summary>
        public static bool LooksLikeSpecialScene(string scene)
        {
            if (string.IsNullOrEmpty(scene))
            {
                return false;
            }
            if (string.Equals(scene, "SHI", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!scene.StartsWith("palace", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (scene.Length <= 6)
            {
                return false;
            }
            for (int i = 6; i < scene.Length; i++)
            {
                if (!char.IsDigit(scene[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Final decision. Whitelisted scenes always join; other scenes join only
        /// when strict mode is off and they look like gameplay (mainline C#levels
        /// or the special SHI / palace modes).
        /// </summary>
        public static bool IsAllowed(string csv, bool strict, string scene)
        {
            if (IsListed(csv, scene))
            {
                return true;
            }
            if (strict)
            {
                return false;
            }
            return LooksLikeGameplayScene(scene) || LooksLikeSpecialScene(scene);
        }

        /// <summary>Why a scene was refused, for the trace line.</summary>
        public static string Describe(string csv, bool strict, string scene)
        {
            if (scene == null || scene.Length == 0)
            {
                return "scene key is empty";
            }
            if (IsListed(csv, scene))
            {
                return "whitelisted";
            }
            if (strict)
            {
                return "not in " + EnvList + " (strict mode)";
            }
            if (LooksLikeGameplayScene(scene) || LooksLikeSpecialScene(scene))
            {
                return "legacy heuristic accepted; add it to " + EnvList + " before trusting this run";
            }
            return "not a gameplay scene";
        }
    }
}
