using System.Text.Json;
using Shared.Mcp;

namespace ClientPlugin;

// take_screenshot, on the client only: a dedicated server renders nothing, so it doesn't offer the
// tool at all.
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

        // The main lane is asked only whether the game has loaded; ScreenshotService does the rest.
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
