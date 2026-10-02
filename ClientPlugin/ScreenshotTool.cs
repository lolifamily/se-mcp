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

    // One capture at a time goes unsaid: ScreenshotService is a single slot (as MyRender11's screenshot
    // is), and a second concurrent call is refused with "retry shortly" — all a model needs to know, at
    // the moment it needs it.
    public string SchemaJson { get; } = ToolSchema.Build("take_screenshot",
        "Capture the current game frame as an image.",
        new ToolSchema.Param("ignore_sprites", "The 3D scene only, without HUD/GUI overlays.",
            type: "boolean", @default: false));

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string errorMessage)
    {
        errorCode = 0;
        errorMessage = null;

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
                errorMessage = "Invalid params: ignore_sprites must be a boolean";
                return false;
            }
            ignoreSprites = sEl.ValueKind == JsonValueKind.True;
        }

        // The executor is borrowed for the Initialized gate; ScreenshotService
        // issues from the main-lane pump, not through Enqueue.
        if (!mainExec.Initialized)
        {
            errorCode = -32002;
            errorMessage = "Game still loading — retry shortly.";
            return false;
        }

        ScreenshotService.Begin(item, ignoreSprites);
        return true;
    }
}
