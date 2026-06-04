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
[HarmonyPatch(typeof(MyRenderThread), "RenderFrame")]
internal static class PatchRenderFrame
{
    private static void Postfix()
    {
        if (Thread.CurrentThread != MyRenderProxy.RenderSystemThread) return;
        Plugin.RenderExecutor?.Tick();
    }
}
