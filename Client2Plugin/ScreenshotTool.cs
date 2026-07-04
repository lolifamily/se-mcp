using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using HarmonyLib;
using Keen.VRage.Core;
using Keen.VRage.Core.Platform;
using Keen.VRage.DCS.Components;
using Keen.VRage.Library.Filesystem;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Library.Utils;
using Keen.VRage.Render.EngineComponents;
using Shared.Mcp;

namespace Client2Plugin;

// SE2 take_screenshot. Where SE1 needs a 185-line single-slot ScreenshotService plus a
// Patch_ScreenshotTaken render-thread callback (MyRenderProxy.TakeScreenshot is fire-and-
// forget), SE2's MainRenderTarget.TakeScreenshotAsync returns a Task — one await, no future
// plumbing. Fire the async capture, and when it completes publish the file path into
// item.Output (ITool's ReturnsImage=true then has McpServer read + encode it, same path SE1
// uses — McpServer is untouched).
//
// SE2 has NO user-facing screenshot feature and no Screenshots root (RootPath is only
// AppData/Content/Temp). The only in-game screenshots are passive (blueprint thumbnails,
// world-save thumbnails) and they write to FileSystem.Temp — read-then-discard. So a Temp
// subdir (Temp/SeMcp/) is the correct home for these LLM-facing captures: transient by nature,
// namespaced so they don't litter the Temp root.
//
// Downsample bounds the image the way SE1's MaxPixels did — NOT primarily for the 10MB wire
// limit (JPG handles that) but to cap the pixel count fed to the LLM's vision input (SE1's
// note: "mirrors the vision input budget"). Resolution comes from IPlatformWindows.Window
// .ClientSize — the game's own display-resolution source — via the public EntityFunctions.Single
// extension, no reflection. .jpg picks the JPG encoder (double safety on size).
public sealed class ScreenshotTool(Executor mainExec) : ITool
{
    // Temp subdirectory for our captures. Created through the game filesystem API (cross-platform).
    private const string SubDir = "SeMcp";

    // ~3.75 MP — SE1's cap, sized to the vision-input token budget rather than the 10MB wire
    // limit. A 1080p frame (2.07 MP) is already under it; 4K (8.3 MP) downsamples to ~2580x1451.
    private const double MaxPixels = 3_750_656;

    public string Name => "take_screenshot";
    public bool ReturnsImage => true;

    public string SchemaJson =>
        """{"name":"take_screenshot","description":"Capture the current game frame and return it as an image.","inputSchema":{"type":"object","properties":{"ignore_sprites":{"type":"boolean","description":"true = capture the 3D scene only, without HUD/GUI overlays. Default false (HUD included)."}}}}""";

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string errorMessage)
    {
        errorCode = 0;
        errorMessage = null;

        // Absent arguments / absent flag / JSON null stay lenient (default: HUD included). A
        // present ignore_sprites of any other shape is rejected — same rule as ExecuteCodeTool.
        var ignoreSprites = false;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("ignore_sprites", out var sEl)
            && sEl.ValueKind != JsonValueKind.Null)
        {
            if (sEl.ValueKind != JsonValueKind.True && sEl.ValueKind != JsonValueKind.False)
            {
                errorCode = -32602;
                errorMessage = "Invalid params: ignore_sprites must be a boolean";
                return false;
            }
            ignoreSprites = sEl.ValueKind == JsonValueKind.True;
        }

        // The executor is borrowed only for the Initialized gate (game-still-loading check);
        // the capture does not go through Executor.Enqueue.
        if (!mainExec.Initialized)
        {
            errorCode = -32002;
            errorMessage = "Game is still loading, not all plugins have been initialized yet. Please retry shortly.";
            return false;
        }

        _ = CaptureAsync(item, ignoreSprites);
        return true;
    }

    private static async Task CaptureAsync(WorkItem item, bool ignoreSprites)
    {
        try
        {
            var target = RenderEngineComponent.Instance.RenderContracts.GetMainTarget();
            EnsureSubDir();

            // Path.Combine keeps the relative separator correct on Windows and Linux; the .jpg
            // extension selects the JPG encoder. Name is just a timestamp — the SeMcp subdir
            // already namespaces it.
            // InvariantCulture: the numeric format emits native digits under
            // fa-IR / ar-SA etc., which would mangle the filename — pin ASCII.
            var relative = Path.Combine(SubDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".jpg");
            var handle = FileSystem.Temp.GetFileHandleWritable(relative);

            // downsample to MaxPixels (aspect-preserving; null = within budget / unknown res),
            // viewport=null, withoutUi=ignoreSprites, awaitWrite=true so the file is complete
            // on disk when the await returns.
            await target.TakeScreenshotAsync(handle, ComputeDownsample(), null, ignoreSprites, awaitWrite: true);

            // GetAbsolutePath requires the file to exist (else FileNotFoundException — the earlier
            // failure); call it AFTER the await, where awaitWrite:true guarantees it's on disk now.
            // ReturnsImage=true → McpServer reads this path, base64-encodes the bytes, emits an image.
            item.Output = handle.GetAbsolutePath();
            Complete(item);
        }
        catch (Exception ex)
        {
            Complete(item, $"screenshot failed: {ex.Message}");
        }
    }

    // Ensure Temp/SeMcp exists. TempStorageManager (FileSystem.Temp) exposes only
    // GetFileHandleWritable/DirectoryExists, not CreateDirectory; the RootFileSystem that does is
    // behind the internal FileSystem.GetFileSystem, so reflect that one step (same internal-access
    // pattern as SessionGate). RootFileSystem.CreateDirectory is public and resolves the absolute
    // path per-platform — nothing is hand-concatenated.
    private static void EnsureSubDir()
    {
        var rootFs = (RootFileSystem)AccessTools
            .Method(typeof(FileSystem), "GetFileSystem", [typeof(RootPath)])
            .Invoke(Singleton<FileSystem>.Instance, [RootPath.Temp]);
        rootFs?.CreateDirectory(SubDir);
    }

    // null → no downsampling (already within the pixel budget, or resolution unknown — JPG
    // still bounds the wire size). Otherwise an aspect-preserving target under MaxPixels.
    private static Vector2I? ComputeDownsample()
    {
        var res = TryGetRenderResolution();
        if (res == null)
            return null;

        var r = res.Value;
        var pixels = (double)r.X * r.Y;
        if (pixels <= MaxPixels || r.X <= 0 || r.Y <= 0)
            return null;

        var scale = Math.Sqrt(MaxPixels / pixels);
        return new Vector2I(Math.Max(1, (int)(r.X * scale)), Math.Max(1, (int)(r.Y * scale)));
    }

    // Current display resolution via the game's own public source: Singleton<VRageCore> →
    // Engine → Single<IPlatformWindows>() → Window (IPlatformWindow) → ClientSize (Vector2I).
    // Single<T> is the public EntityFunctions.Single extension (Keen.VRage.DCS.Components); this
    // is exactly what AnalyticsEventGameMetadataProviderComponent reads for its DisplayResolution
    // metric — all public, no reflection. Failure → null (skip downsample; JPG still bounds size).
    private static Vector2I? TryGetRenderResolution()
    {
        try
        {
            var platform = Singleton<VRageCore>.Instance.Engine.Single<IPlatformWindows>();
            return platform.Window.ClientSize;
        }
        catch
        {
            // ignored — treat as unknown resolution
        }
        return null;
    }

    private static void Complete(WorkItem item, string error = null)
    {
        item.Error = error;
        item.Done.TrySetResult(true);
    }
}
