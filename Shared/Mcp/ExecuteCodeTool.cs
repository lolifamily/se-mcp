using System.Collections.Generic;
using System.Text.Json;

namespace Shared.Mcp;

// The execute_code tool. Client supplies both main and render executors; server
// supplies only main (renderExec = null). A null render executor collapses the
// schema (target enum loses "render") and rejects any "render" target with -32602.
//
// mpAdminNote: appended verbatim (with a leading space) to the top-level
// description so the LLM is warned about MP gating before it composes a
// request that the executor would only reject after dispatch. null/empty = no suffix.
public sealed class ExecuteCodeTool(
    Executor mainExec,
    Executor renderExec = null,
    string mpAdminNote = null) : ITool
{
    public string Name => "execute_code";
    public bool ReturnsImage => false;
    public string SchemaJson { get; } = BuildSchema(renderExec != null, mpAdminNote);

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string error)
    {
        errorCode = 0;
        error = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
        {
            errorCode = -32602;
            error = "Invalid params: arguments.code must be a string";
            return false;
        }

        item.Code = codeEl.GetString();

        // class_body: optional string. JSON null treated as absent, same rule as target.
        if (arguments.TryGetProperty("class_body", out var cbEl) && cbEl.ValueKind != JsonValueKind.Null)
        {
            if (cbEl.ValueKind != JsonValueKind.String)
            {
                errorCode = -32602;
                error = "Invalid params: class_body must be a string";
                return false;
            }
            item.ClassBody = cbEl.GetString();
        }

        // usings: optional array of strings. Each item is a namespace path (no "using " prefix, no ";").
        if (arguments.TryGetProperty("usings", out var uEl) && uEl.ValueKind != JsonValueKind.Null)
        {
            if (uEl.ValueKind != JsonValueKind.Array)
            {
                errorCode = -32602;
                error = "Invalid params: usings must be an array of strings";
                return false;
            }
            var usings = new List<string>(uEl.GetArrayLength());
            foreach (var el in uEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    errorCode = -32602;
                    error = "Invalid params: usings items must be strings";
                    return false;
                }
                usings.Add(el.GetString());
            }
            item.Usings = usings;
        }

        // target: routes to main (default) or render. JSON null = absent.
        // The string never enters WorkItem / Executor — it's resolved to a
        // concrete Executor instance here and discarded. Any non-string shape
        // is rejected rather than silently falling back to "main".
        string target = null;
        if (arguments.TryGetProperty("target", out var tEl) && tEl.ValueKind != JsonValueKind.Null)
        {
            if (tEl.ValueKind != JsonValueKind.String)
            {
                errorCode = -32602;
                error = "Invalid params: target must be a string";
                return false;
            }
            target = tEl.GetString();
        }

        var executor = target switch
        {
            null or "" or "main" => mainExec,
            "render" => renderExec, // null when server-side → caught below
            _ => null
        };
        if (executor == null)
        {
            errorCode = -32602;
            error = renderExec != null
                ? $"Invalid params: target must be \"main\" or \"render\" (got \"{target}\")"
                : $"Invalid params: target must be \"main\" (got \"{target}\")";
            return false;
        }

        if (!executor.Initialized)
        {
            errorCode = -32002;
            error = "Game is still loading, not all plugins have been initialized yet. Please retry shortly.";
            return false;
        }

        executor.Enqueue(item);
        return true;
    }

    // --- schema construction ---------------------------------------------------
    //
    // Single template + three Replace passes. The two lane configurations
    // (with-render / main-only) live as literal constants below; pick by bool.
    private const string SchemaTemplate = """
{"name":"execute_code","description":"Execute C# in Space Engineers. Full .NET + game API access. Three fields map 1:1 to C# language layers: `code` is the entry method body (statements only), `class_body` holds class-level declarations (methods/fields/nested types/[DllImport]), `usings` adds namespace imports. Pre-imported namespaces: System.*, VRage.*, VRageMath, Sandbox.*, SpaceEngineers.Game.*. ALWAYS use short type names like MySession.Static, MyCubeGrid — do NOT write fully qualified names like Sandbox.Game.World.MySession.Static.<<MP_NOTE>> Errors are reported as `<field> (line,col): error CSxxxx: msg` so you know which input field to fix.","inputSchema":{"type":"object","properties":{"code":{"type":"string","description":"Entry method body — STATEMENTS ONLY. Goes inside the wrapper Run() method. Use Console.WriteLine() for output. Use `yield return null` to pause until the next frame. Do NOT put `using` directives or class-level declarations here — use `usings` and `class_body` for those."},"class_body":{"type":"string","description":"OPTIONAL. Class-level declarations spliced into the wrapper class body alongside Run(): methods, fields, properties, nested types, [DllImport] P/Invoke. Use this when you need attributes that cannot go on statements (e.g. [DllImport]). Items declared here are referenced from `code` directly (same class). Most scripts leave this empty."},"usings":{"type":"array","items":{"type":"string"},"description":"OPTIONAL. Extra namespace imports beyond the defaults. Each item is a bare namespace path like \"System.Runtime.InteropServices\", an alias like \"IO = System.IO\", or \"static System.Math\". Do NOT include the `using` keyword or trailing semicolon — they are added automatically."},"target":{"type":"string","enum":<<LANE_ENUM>>,"description":"<<TARGET_DESC>>"}},"required":["code"]}}
""";

    private const string TargetDescLongForm =
        "Execution lane. \\\"main\\\" (default) runs in the game's main thread via IPlugin.Update — " +
        "use this for MyAPIGateway / Session / Grid / Entity access. " +
        "\\\"render\\\" runs in the render thread via a Harmony Postfix on MyRenderThread.RenderFrame — " +
        "use ONLY to inspect other plugins' Harmony hooks that execute on the render thread " +
        "(their __instance, captured locals, accumulated fields). " +
        "Render-target scripts freeze one frame per step (~16ms); use yield return null to split work across frames. " +
        "MyAPIGateway will assert-throw on render thread.";

    private const string TargetDescMainOnly =
        "Execution lane. Only \\\"main\\\" is supported here — runs in the game's main thread via IPlugin.Update. " +
        "Use this for MyAPIGateway / Session / Grid / Entity access.";

    private static string BuildSchema(bool hasRenderLane, string mpAdminNote)
    {
        var laneEnum = hasRenderLane ? """["main","render"]""" : """["main"]""";
        var targetDesc = hasRenderLane ? TargetDescLongForm : TargetDescMainOnly;
        var mpNote = string.IsNullOrEmpty(mpAdminNote) ? "" : " " + mpAdminNote;

        return SchemaTemplate
            .Replace("<<LANE_ENUM>>", laneEnum)
            .Replace("<<TARGET_DESC>>", targetDesc)
            .Replace("<<MP_NOTE>>", mpNote);
    }
}
