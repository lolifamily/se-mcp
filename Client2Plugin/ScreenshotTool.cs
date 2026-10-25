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

// SE2's take_screenshot. TakeScreenshotAsync returns a Task, where SE1's TakeScreenshot is fire and
// forget, so this needs none of SE1's single-slot service: the capture is awaited, and its file's
// path goes into item.Output for McpServer to send.
//
// Into Temp\SeMcp\: captures for a model are transient, SE2 has no screenshot feature or folder (its
// roots are AppData, Content and Temp), and its own captures, blueprint and save thumbnails, go to
// Temp as well.
public sealed class ScreenshotTool(Executor mainExec) : ITool
{
    // Under Temp, created through the game's file system, which knows where Temp is on each platform.
    private const string SubDir = "SeMcp";

    // ~3.75 MP, SE1's cap: sized to the vision-input token budget rather than the 10MB wire
    // limit. A 1080p frame (2.07 MP) is already under it; 4K (8.3 MP) downsamples to ~2580x1451.
    private const double MaxPixels = 3_750_656;

    public string Name => "take_screenshot";
    public bool ReturnsImage => true;

    public string SchemaJson { get; } = ToolSchema.Build("take_screenshot",
        "Capture the current game frame as an image.",
        new ToolSchema.Param("ignore_sprites", "The 3D scene only, without HUD/GUI overlays.",
            type: "boolean", @default: false));

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string errorMessage)
    {
        errorCode = 0;
        errorMessage = null;

        // Absent or null means the default, the HUD included; any other non-boolean is refused, as in
        // ExecuteCodeTool.
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

        // The main lane is asked only whether the game has loaded; the capture doesn't run on it.
        if (!mainExec.Initialized)
        {
            errorCode = -32002;
            errorMessage = "Game still loading — retry shortly.";
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

            // .jpg picks the JPEG encoder. InvariantCulture: under fa-IR, ar-SA and the like the
            // format would write native digits into the name.
            var relative = Path.Combine(SubDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".jpg");
            var handle = FileSystem.Temp.GetFileHandleWritable(relative);

            // downsample (null: as is), viewport, withoutUi; awaitWrite: the file is complete once
            // the await returns.
            await target.TakeScreenshotAsync(handle, ComputeDownsample(), null, ignoreSprites, awaitWrite: true);

            // After the await: GetAbsolutePath throws FileNotFoundException until the file exists.
            item.Output = handle.GetAbsolutePath();
            Complete(item);
        }
        catch (Exception ex)
        {
            Complete(item, $"screenshot failed: {ex.Message}");
        }
    }

    // FileSystem.Temp, a TempStorageManager, can't create a directory. The RootFileSystem behind it
    // can, reached only through the internal FileSystem.GetFileSystem, hence the reflection.
    private static void EnsureSubDir()
    {
        var rootFs = (RootFileSystem)AccessTools
            .Method(typeof(FileSystem), "GetFileSystem", [typeof(RootPath)])
            .Invoke(Singleton<FileSystem>.Instance, [RootPath.Temp]);
        rootFs?.CreateDirectory(SubDir);
    }

    // Null when the frame is within MaxPixels, or its size is unknown and JPEG alone bounds the file;
    // otherwise the size that fits, same aspect.
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

    // The window's client size, where the game's own analytics read the display resolution. Null
    // when it can't be had.
    private static Vector2I? TryGetRenderResolution()
    {
        try
        {
            var platform = Singleton<VRageCore>.Instance.Engine.Single<IPlatformWindows>();
            return platform.Window.ClientSize;
        }
        catch
        {
            // unknown resolution
        }
        return null;
    }

    private static void Complete(WorkItem item, string error = null)
    {
        item.Error = error;
        item.Done.TrySetResult(true);
    }
}
