using HarmonyLib;
using VRageRender;

namespace ClientPlugin.Patches;

// Every screenshot-save implementation funnels through MyRenderProxy.ScreenshotTaken
// after the file handle is closed — the stock MyTextureData.ToFile path and plugins
// replacing SaveScreenshotFromResource (e.g. HDR renderers) alike. A Postfix here is
// the only first-hand completion signal that carries the exact filename and success
// flag; the public MySandboxGame.OnScreenshotTaken event drops both.
//
// Runs on the render thread (stock) or a saver's background task, so the handler
// must stay thread-safe and cheap: NotifyScreenshotTaken only compares the path
// and flips the pending slot — no IO on this thread.
[HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.ScreenshotTaken))]
internal static class PatchScreenshotTaken
{
    private static void Postfix(bool success, string filename)
    {
        ScreenshotService.NotifyScreenshotTaken(success, filename);
    }
}
