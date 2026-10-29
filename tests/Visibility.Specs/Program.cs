using System;
using IceyCoop.GameBridge;
using IceyCoop.GameBridge.World;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL: " + name); }
    private static Renderer[] NewVisuals(params bool[] states) { var go = new GameObject(); go.renderers = Array.ConvertAll(states, x => new Renderer { enabled = x, owner = go }); return go.renderers; }
    private static void Main()
    {
        BridgeContext.SetRole(true, false, false);
        var p1 = NewVisuals(true, false); R.Player = new Player { transform = new Transform { gameObject = p1[0].owner } };
        ClientIsolation.HideLocalPlayerVisual(); Check(!p1[0].enabled, "P1 hidden");
        BridgeContext.SetRole(false, true, false); Check(p1[0].enabled, "P1 restored on host"); Check(!p1[1].enabled, "original disabled stays disabled");

        BridgeContext.SetRole(true, false, false); ClientIsolation.HideLocalPlayerVisual(); ClientIsolation.HideLocalPlayerVisual();
        BridgeContext.SetRole(false, true, false); Check(p1[0].enabled, "repeated hide restores once");
        BridgeContext.SetRole(true, false, false); ClientIsolation.HideLocalPlayerVisual(); BridgeContext.SetRole(false, false, true); Check(p1[0].enabled, "offline restores");
        BridgeContext.SetRole(true, false, false); ClientIsolation.HideLocalPlayerVisual(); Check(!p1[0].enabled, "can hide again after restore"); BridgeContext.SetRole(false, true, false);

        BridgeContext.SetRole(true, false, false); LevelManager.SceneName = "SHI"; var shi = NewVisuals(true); PlayerControllerDemo.Instance = new PlayerControllerDemo { gameObject = shi[0].owner };
        ClientIsolation.HideLocalShiVisual(); Check(!shi[0].enabled, "SHI hidden"); BridgeContext.SetRole(false, true, false); Check(shi[0].enabled, "SHI restored");
        BridgeContext.SetRole(true, false, false); var replacement = NewVisuals(true); R.Player = new Player { transform = new Transform { gameObject = replacement[0].owner } }; ClientIsolation.HideLocalPlayerVisual(); BridgeContext.SetRole(false, true, false); Check(replacement[0].enabled, "replacement restored");
        BridgeContext.SetRole(true, false, false); var destroyed = NewVisuals(true, true); R.Player = new Player { transform = new Transform { gameObject = destroyed[0].owner } }; ClientIsolation.HideLocalPlayerVisual(); destroyed[0].Destroyed = true; BridgeContext.SetRole(false, true, false); Check(!destroyed[0].enabled, "destroyed renderer ignored"); Check(destroyed[1].enabled, "live renderer restored beside destroyed renderer");
        Console.WriteLine("PASS " + checks + " visibility assertions");
    }
}
