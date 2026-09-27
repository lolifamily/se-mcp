using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Shared.Mcp;

// Host-specific vocabulary spliced into the execute_code schema description. The schema
// shape (three fields, statements-only, error format, target routing) is a mechanical
// contract shared by SE1 and SE2; only these few symbols name the concrete game API and
// differ per host. SE1 passes null (→ Se1Default below); SE2 passes
// Shared.Se2.ScriptDefaults.SchemaText.
// Primary-constructor class (NOT a record): record's init accessors need
// System.Runtime.CompilerServices.IsExternalInit, which net48 (the SE1 targets) lacks.
// get-only auto-props + a primary ctor compile on both net48 and net10.
public sealed class ExecuteCodeSchemaText(
    string preImported, string shortNames, string fqnExample,
    string mainDriver, string mainApi, string renderTarget, string renderAssert)
{
    public string PreImported { get; } = preImported;    // "System.*, VRage.*, Sandbox.*"
    public string ShortNames { get; } = shortNames;      // "MySession.Static, MyCubeGrid"
    public string FqnExample { get; } = fqnExample;      // "Sandbox.Game.World.MySession.Static"
    public string MainDriver { get; } = mainDriver;      // "IPlugin.Update"
    public string MainApi { get; } = mainApi;            // "MyAPIGateway / Session / Grid / Entity"
    public string RenderTarget { get; } = renderTarget;  // "MyRenderThread.RenderFrame"
    public string RenderAssert { get; } = renderAssert;  // "MyAPIGateway"
}

// The execute_code tool. Every host has the main and parallel lanes; the client adds render
// (renderExec = null on the server). One lane list drives the schema's target enum, the target
// description and dispatch, so the three can't drift apart; a target not on it gets -32602.
//
// mpAdminNote: appended verbatim (with a leading space) to the top-level
// description so the LLM is warned about MP gating before it composes a
// request that the executor would only reject after dispatch. null/empty = no suffix.
//
// vocab: host-specific schema symbols (see ExecuteCodeSchemaText). null → Se1Default,
// so SE1 needs no change; SE2 passes its own.
public sealed class ExecuteCodeTool : ITool
{
    public string Name => "execute_code";
    public bool ReturnsImage => false;
    public string SchemaJson { get; }

    // "main" first: the default, and first in the schema's enum.
    private readonly (string Name, IScriptLane Lane, string Desc)[] lanes;

    public ExecuteCodeTool(
        Executor mainExec,
        ParallelExecutor parallelExec,
        Executor renderExec = null,
        string mpAdminNote = null,
        ExecuteCodeSchemaText vocab = null)
    {
        lanes = renderExec == null
            ? [("main", mainExec, MainLaneDesc), ("parallel", parallelExec, ParallelLaneDesc)]
            : [("main", mainExec, MainLaneDesc), ("render", renderExec, RenderLaneDesc), ("parallel", parallelExec, ParallelLaneDesc)];
        SchemaJson = BuildSchema(lanes, mpAdminNote, vocab ?? Se1Default);
    }

    // SE1 back-compat default — the exact symbols the schema shipped with before it was
    // parameterized. Passing vocab=null (both SE1 hosts) keeps them.
    private static readonly ExecuteCodeSchemaText Se1Default = new(
        preImported: "System.*, VRage.*, VRageMath, Sandbox.*, SpaceEngineers.Game.*",
        shortNames: "MySession.Static, MyCubeGrid",
        fqnExample: "Sandbox.Game.World.MySession.Static",
        mainDriver: "IPlugin.Update",
        mainApi: "MyAPIGateway / Session / Grid / Entity",
        renderTarget: "MyRenderThread.RenderFrame",
        renderAssert: "MyAPIGateway");

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string errorMessage)
    {
        errorCode = 0;
        errorMessage =null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
        {
            errorCode = -32602;
            errorMessage ="Invalid params: arguments.code must be a string";
            return false;
        }

        item.Code = codeEl.GetString();

        // class_body: optional string. JSON null treated as absent, same rule as target.
        if (arguments.TryGetProperty("class_body", out var cbEl) && cbEl.ValueKind != JsonValueKind.Null)
        {
            if (cbEl.ValueKind != JsonValueKind.String)
            {
                errorCode = -32602;
                errorMessage ="Invalid params: class_body must be a string";
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
                errorMessage ="Invalid params: usings must be an array of strings";
                return false;
            }
            var usings = new List<string>(uEl.GetArrayLength());
            foreach (var el in uEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    errorCode = -32602;
                    errorMessage ="Invalid params: usings items must be strings";
                    return false;
                }
                usings.Add(el.GetString());
            }
            item.Usings = usings;
        }

        // target: a lane name; absent, JSON null or "" means the default. The string never
        // enters WorkItem / Executor — it's resolved to a lane here and discarded. Any
        // non-string shape is rejected rather than silently falling back to "main".
        string target = null;
        if (arguments.TryGetProperty("target", out var tEl) && tEl.ValueKind != JsonValueKind.Null)
        {
            if (tEl.ValueKind != JsonValueKind.String)
            {
                errorCode = -32602;
                errorMessage ="Invalid params: target must be a string";
                return false;
            }
            target = tEl.GetString();
        }

        var lane = string.IsNullOrEmpty(target) ? lanes[0].Lane : Array.Find(lanes, l => l.Name == target).Lane;
        if (lane == null)
        {
            errorCode = -32602;
            errorMessage =$"Invalid params: target must be one of {string.Join(", ", lanes.Select(l => $"\"{l.Name}\""))} (got \"{target}\")";
            return false;
        }

        if (!lane.Initialized)
        {
            errorCode = -32002;
            errorMessage ="Game is still loading, not all plugins have been initialized yet. Please retry shortly.";
            return false;
        }

        lane.Enqueue(item);
        return true;
    }

    // --- schema construction ---------------------------------------------------
    //
    // Single template + Replace passes. Host-neutral wording stays literal; every
    // host-specific symbol is a <<PLACEHOLDER>> filled from the vocab. The target enum and
    // description are assembled from the host's lanes.
    private const string SchemaTemplate = """
{"name":"execute_code","description":"Execute C# in Space Engineers. Full .NET + game API access, including the game's INTERNAL types and members — scripts compile with ignore-accessibility, so internal classes/methods/fields/properties are directly usable WITHOUT reflection (only truly private members still need reflection). Three fields map 1:1 to C# language layers: `code` is the entry method body (statements only), `class_body` holds class-level declarations (methods/fields/nested types/[DllImport]), `usings` adds namespace imports. Pre-imported namespaces: <<PREIMPORTED>>. ALWAYS use short type names like <<SHORTNAMES>> — do NOT write fully qualified names like <<FQN_EXAMPLE>>.<<MP_NOTE>>","inputSchema":{"type":"object","properties":{"code":{"type":"string","description":"Entry method body — STATEMENTS ONLY. Goes inside the wrapper Run() method. Use Console.WriteLine() for output. Use `yield return null` to pause until the next frame. Do NOT put `using` directives or class-level declarations here — use `usings` and `class_body` for those."},"class_body":{"type":"string","description":"OPTIONAL. Class-level declarations spliced into the wrapper class body alongside Run(): methods, fields, properties, nested types, [DllImport] P/Invoke. Use this when you need attributes that cannot go on statements (e.g. [DllImport]). Items declared here are referenced from `code` directly (same class). Most scripts leave this empty."},"usings":{"type":"array","items":{"type":"string"},"description":"OPTIONAL. Extra namespace imports beyond the defaults. Each item is a bare namespace path like \"System.Runtime.InteropServices\", an alias like \"IO = System.IO\", or \"static System.Math\". Do NOT include the `using` keyword or trailing semicolon — they are added automatically."},"target":{"type":"string","enum":<<LANE_ENUM>>,"description":"<<TARGET_DESC>>"}},"required":["code"]}}
""";

    private const string MainLaneDesc =
        "\\\"main\\\" (default) runs in the game's main thread via <<MAIN_DRIVER>> — " +
        "use this for <<MAIN_API>> access.";

    private const string RenderLaneDesc =
        "\\\"render\\\" runs in the render thread via a Harmony Postfix on <<RENDER_TARGET>> — " +
        "use ONLY to inspect other plugins' Harmony hooks that execute on the render thread " +
        "(their __instance, captured locals, accumulated fields). " +
        "Render-target scripts freeze one frame per step (~16ms); use yield return null to split work across frames. " +
        "<<RENDER_ASSERT>> will assert-throw on render thread.";

    private const string ParallelLaneDesc =
        "\\\"parallel\\\" runs off-frame and concurrently, with no frame budget — for computation and blocking I/O.";

    private static string BuildSchema(
        (string Name, IScriptLane Lane, string Desc)[] lanes, string mpAdminNote, ExecuteCodeSchemaText v)
    {
        var laneEnum = "[" + string.Join(",", lanes.Select(l => $"\"{l.Name}\"")) + "]";
        var targetDesc = ("Execution lane. " + string.Join(" ", lanes.Select(l => l.Desc)))
            .Replace("<<MAIN_DRIVER>>", v.MainDriver)
            .Replace("<<MAIN_API>>", v.MainApi)
            .Replace("<<RENDER_TARGET>>", v.RenderTarget)
            .Replace("<<RENDER_ASSERT>>", v.RenderAssert);
        var mpNote = string.IsNullOrEmpty(mpAdminNote) ? "" : " " + mpAdminNote;

        return SchemaTemplate
            .Replace("<<PREIMPORTED>>", v.PreImported)
            .Replace("<<SHORTNAMES>>", v.ShortNames)
            .Replace("<<FQN_EXAMPLE>>", v.FqnExample)
            .Replace("<<LANE_ENUM>>", laneEnum)
            .Replace("<<TARGET_DESC>>", targetDesc)
            .Replace("<<MP_NOTE>>", mpNote);
    }
}
