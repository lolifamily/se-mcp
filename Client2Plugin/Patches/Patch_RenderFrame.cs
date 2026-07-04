using System.Threading;
using HarmonyLib;

namespace Client2Plugin.Patches;

// Drive the render lane from a Postfix on Render12EngineComponent.RenderFrame (a private
// instance method on an internal type; Harmony patches it by name). Postfix rather than
// Prefix so the script runs after this frame's Draw/Present: if it perturbs D3D state, the
// next frame's setup rebinds and the damage is absorbed.
//
// Thread guard: in synchronous-render mode (RenderThreadManager.InitRenderThreadAsMainThread)
// RenderFrame runs on the MAIN thread; ticking the render lane there would double-tick with
// the main lane on a confused thread identity. We gate on the render-thread identity.
//
// Confirmed at runtime (block 4): SE2's render thread is named "Render thread" (a distinct
// thread — id 81 vs the main thread's id 2 in testing), so this gate passes only there. In
// synchronous-render mode a render-targeted request fails safe with -32002 from the McpServer
// rather than ticking on the wrong thread. Mirrors SE1's PatchRenderFrame, which gates on
// Thread.CurrentThread == MyRenderProxy.RenderSystemThread.
// Render12EngineComponent is internal — patch by string type name (Harmony resolves it via
// AccessTools.TypeByName at runtime) rather than typeof, which won't compile across assemblies.
[HarmonyPatch("Keen.VRage.Render12.EngineComponents.Render12EngineComponent", "RenderFrame")]
internal static class PatchRenderFrame
{
    private static void Postfix()
    {
        if (Thread.CurrentThread.Name != "Render thread")
            return;

        var exec = Plugin.RenderExecutor;
        if (exec == null)
            return;
        if (!exec.Initialized)
        {
            // Gate on the main lane's pump being alive first — its InitShared is what
            // publishes the shared compiler refs to this thread.
            if (Plugin.MainExecutor?.Initialized != true)
                return;
            exec.Initialize();
        }
        exec.Tick();
    }
}
