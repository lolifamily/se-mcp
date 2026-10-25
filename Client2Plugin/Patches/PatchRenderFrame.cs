using System.Threading;
using HarmonyLib;

namespace Client2Plugin.Patches;

// Steps the render lane after each frame's Draw and Present, as on SE1. Render12EngineComponent is
// internal and RenderFrame private, so both go by name.
//
// Checked at runtime: SE2's render thread is a thread of its own, named "Render thread". In
// synchronous rendering (RenderThreadManager.InitRenderThreadAsMainThread) RenderFrame runs on the
// main thread, where this test keeps the lane closed: render requests get -32002.
[HarmonyPatch("Keen.VRage.Render12.EngineComponents.Render12EngineComponent", "RenderFrame")]
internal static class PatchRenderFrame
{
    [HarmonyPriority(int.MinValue)]
    private static void Postfix()
    {
        if (Thread.CurrentThread.Name != "Render thread")
            return;

        var exec = Plugin.RenderExecutor;
        if (exec == null)
            return;
        if (!exec.Initialized)
        {
            // After the main lane: its opening publishes the script references to this thread.
            if (Plugin.MainExecutor?.Initialized != true)
                return;
            exec.Initialize();
        }
        exec.Tick();
    }
}
