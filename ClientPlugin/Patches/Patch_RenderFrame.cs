using System.Threading;
using HarmonyLib;
using VRageRender;
using VRageRender.ExternalApp;

namespace ClientPlugin.Patches;

// Hook the render thread's per-frame entry to drive RenderExecutor.Tick. Postfix
// (rather than Prefix) so that the script runs after this frame's Draw/Present:
// if the script accidentally perturbs D3D11 ImmediateContext state, the next
// frame's BeforeRender will rebind and the damage is absorbed; a Prefix would
// dirty state immediately before Draw.
//
// Private instance method on a publicly-visible type — Harmony patches it via
// string name. RenderFrame is called every frame except a couple of early-return
// paths (Ansel capture, render suspended), which is acceptable: those paths
// occur during specific debug/suspend states where REPL liveness doesn't matter.
//
// The thread check guards against the synchronous-render path (StartSync) where
// MyRenderThread runs on the main thread; in that mode the main Executor is the
// right one to tick and we'd otherwise double-tick scripts on a confused thread
// identity. RenderSystemThread is null in sync mode.
//
// Initialize lives here, on the lane's own pump, NOT in Plugin.Update: the flag
// means "this lane's pump is alive". In StartSync mode this hook never passes
// the thread gate, so render-targeted requests keep getting -32002 from the
// McpServer instead of compiling into a queue nothing ever drains; a patch
// broken by a game update fails the same safe way. Until the lane is open,
// early-exit — no Initialize, no Tick (nothing to drain either: the McpServer
// gate keeps `active` empty before Initialized flips). The gate on
// MainExecutor.Initialized is load-bearing: that volatile write — set AFTER
// Compiler.InitShared returns on the main thread — is what publishes the
// shared compiler references to this thread (see Compiler._sharedInit notes).
[HarmonyPatch(typeof(MyRenderThread), "RenderFrame")]
internal static class PatchRenderFrame
{
    private static void Postfix()
    {
        if (Thread.CurrentThread != MyRenderProxy.RenderSystemThread) return;
        var exec = Plugin.RenderExecutor;
        if (exec == null) return;
        if (!exec.Initialized)
        {
            if (Plugin.MainExecutor?.Initialized != true) return;
            exec.Initialize();
        }
        exec.Tick();
    }
}
