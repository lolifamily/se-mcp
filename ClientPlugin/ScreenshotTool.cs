using System.Text.Json;
using Shared.Mcp;

namespace ClientPlugin;

// Client-only ITool — server has no render thread, no MyRenderProxy.TakeScreenshot,
// no point shipping a "screenshot is unavailable here" error path. The Executor
// reference is borrowed only to gate on Initialized (game-still-loading check);
// the WorkItem is handed to ScreenshotService.Begin which runs its own service
// loop, NOT through Executor.Enqueue.
public sealed class ScreenshotTool(Executor mainExec) : ITool
{
    public string Name => "take_screenshot";
    public bool ReturnsImage => true;

    public string SchemaJson =>
        """{"name":"take_screenshot","description":"Capture the current game frame and return it as an image. Only one screenshot may be in flight at a time.","inputSchema":{"type":"object","properties":{"ignore_sprites":{"type":"boolean","description":"true = capture the 3D scene only, without HUD/GUI overlays. Default false (HUD included)."}}}}""";

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string error)
    {
        errorCode = 0;
        error = null;

        // Absent arguments / absent flag / JSON null stay lenient (the default:
        // HUD included). A present ignore_sprites of any other shape is rejected
        // — same rule as target in ExecuteCodeTool, no silent fallback.
        var ignoreSprites = false;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("ignore_sprites", out var sEl)
            && sEl.ValueKind != JsonValueKind.Null)
        {
            if (sEl.ValueKind != JsonValueKind.True && sEl.ValueKind != JsonValueKind.False)
            {
                errorCode = -32602;
                error = "Invalid params: ignore_sprites must be a boolean";
                return false;
            }
            ignoreSprites = sEl.ValueKind == JsonValueKind.True;
        }

        // The executor is borrowed for the Initialized gate; ScreenshotService
        // issues from Plugin.Update, not through Enqueue.
        if (!mainExec.Initialized)
        {
            errorCode = -32002;
            error = "Game is still loading, not all plugins have been initialized yet. Please retry shortly.";
            return false;
        }

        ScreenshotService.Begin(item, ignoreSprites);
        return true;
    }
}
