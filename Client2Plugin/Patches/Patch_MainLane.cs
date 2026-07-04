using HarmonyLib;
using Keen.VRage.Core;
using Shared.Mcp;

namespace Client2Plugin.Patches;

// SE2's IPlugin is empty (no per-frame Update), so the main execution lane is driven from
// a Postfix on VRageCore.Update — called once per frame after the frame's DCS jobs have
// joined (the cleanest safe point; doesn't touch session). This does what SE1 does inside
// IPlugin.Update: refresh the deny gate, init the shared compiler, tick the main lane.
[HarmonyPatch(typeof(VRageCore), "Update")]
internal static class PatchMainLane
{
    private static void Postfix()
    {
        // No Instance/Failed check: this patch is applied BY PatchHelpers.HarmonyPatchAll at the
        // very end of construction. If PatchHelpers failed, the patch was never applied and this
        // Postfix never runs — so reaching here already means the plugin loaded successfully and
        // the statics below are populated. (SE1 needs its _failed check because its pump is the
        // native IPlugin.Update, which the game calls regardless of whether patching succeeded; a
        // Harmony-patched pump only exists when patching succeeded, so the check would be moot.)
        // Everything below is static; a null MainExecutor is short-circuited by ?. anyway.

        // Refresh the deny gate on the main thread once per frame. Other threads (Enqueue
        // from the pool, RenderExecutor.Tick) read the cached Config.Denied — bool atomic,
        // at most one frame stale — because they can't safely touch the scene off-thread.
        Config.Current.Denied = SessionGate.IsClientOnlySession();

        // Order matters: InitShared must populate the shared compiler references before
        // MainExecutor.Initialize flips Initialized=true — that volatile write is also what
        // publishes those references across threads (see Compiler._sharedInit notes).
        Compiler.InitShared();
        Plugin.MainExecutor?.Initialize();
        Plugin.MainExecutor?.Tick();
    }
}
