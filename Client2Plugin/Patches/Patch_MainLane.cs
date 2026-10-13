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
        // No Instance/Failed check: this patch is applied BY PatchHelpers.HarmonyPatchAll at the
        // very end of construction. If PatchHelpers failed, the patch was never applied and this
        // Postfix never runs — so reaching here already means the plugin loaded successfully and
        // the statics below are populated. Everything below is static; a null MainExecutor is
        // short-circuited by ?. anyway.

        // Refresh the deny gate on the main thread once per frame. Other threads (Enqueue
        // from the pool, RenderExecutor.Tick) read the cached Config.Denied — bool atomic,
        // at most one frame stale — because they can't safely touch the scene off-thread.
        Config.Current.Denied = SessionGate.IsClientOnlySession();

        // InitShared is true once the shared compiler references, built on a thread of its own,
        // are in; until then nothing below has work. Order matters: they must be in before
        // MainExecutor.Initialize flips Initialized=true — that volatile write is also what
        // publishes those references across threads (see Compiler._sharedReady notes).
        // ParallelExecutor publishes them the same way, and EnforceDenyGate pushes the gate
        // refreshed above onto running scripts. AppContext.BaseDirectory is Game2, the game
        // folder InitShared asks for.
        if (!Compiler.InitShared(AppContext.BaseDirectory)) return;
        Plugin.MainExecutor?.Initialize();
        Plugin.ParallelExecutor?.Initialize();
        Plugin.MainExecutor?.Tick();
        Plugin.ParallelExecutor?.EnforceDenyGate();
    }
}
