using System.Threading;
using HarmonyLib;
using VRageRender;
using VRageRender.ExternalApp;

namespace ClientPlugin.Patches;

// Steps the render lane after each frame's Draw and Present: a script that disturbs the D3D11
// context does so where the next frame's BeforeRender rebinds it anyway, not right before a draw.
// RenderFrame is private, hence the name. A frame it returns from early (Ansel capture, render
// suspended) goes unstepped, which in those states doesn't matter.
//
// The thread test keeps out synchronous rendering (StartSync), where RenderFrame runs on the main
// thread and RenderSystemThread is null. The lane opens here, on its own pump, so in StartSync, or
// with this patch broken by a game update, render requests get -32002 rather than a queue nothing
// drains. It waits for the main lane, whose opening publishes the script references to this thread
// (Compiler._sharedReady).
[HarmonyPatch(typeof(MyRenderThread), "RenderFrame")]
internal static class PatchRenderFrame
{
    [HarmonyPriority(int.MinValue)]
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
