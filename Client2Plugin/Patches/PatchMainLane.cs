using System;
using HarmonyLib;
using Keen.VRage.Core;
using Shared.Mcp;

namespace Client2Plugin.Patches;

// Main lane pump: VRageCore.Update runs once per frame, after the frame's DCS jobs have joined.
// Same work as SE1's Plugin.Pump.
[HarmonyPatch(typeof(VRageCore), "Update")]
internal static class PatchMainLane
{
    [HarmonyPriority(int.MinValue)]
    private static void Postfix()
    {
        // No check that the plugin loaded: the constructor applies this patch last (HarmonyPatchAll),
        // so running at all means it did.

        // Refreshed once a frame, here: the main thread is the one that may look at the session.
        Config.Current.Denied = SessionGate.IsClientOnlySession();

        // The lanes open only once the script references are in (InitShared).
        // AppContext.BaseDirectory is Game2, the game folder InitShared asks for.
        if (!Compiler.InitShared(AppContext.BaseDirectory)) return;
        Plugin.MainExecutor?.Initialize();
        Plugin.ParallelExecutor?.Initialize();
        Plugin.MainExecutor?.Tick();
        Plugin.ParallelExecutor?.EnforceDenyGate();
    }
}
