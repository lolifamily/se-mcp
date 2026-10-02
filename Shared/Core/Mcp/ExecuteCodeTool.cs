using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Shared.Mcp;

// The execute_code tool. Every host has the main and parallel lanes; the client adds render
// (renderExec = null on the server). One lane list drives the schema's target enum and default,
// the target description and dispatch, so they can't drift apart; a target not on it gets -32602.
//
// The schema is read by a model in every session, so it says what the next call needs and stops —
// the Minecraft MCP's McpJson makes the full argument. What it leaves out on purpose is kept in
// comments here, where it costs a model nothing. game and imports are the host's own words
// (Shared.Se1/Se2 ScriptDefaults): its name, and what comes pre-imported — the one thing about
// writing a script that differs between hosts.
public sealed class ExecuteCodeTool : ITool
{
    public string Name => "execute_code";
    public bool ReturnsImage => false;
    public string SchemaJson { get; }

    // "main" first: the default, and first in the schema's enum.
    private readonly (string Name, IScriptLane Lane, string Prose)[] lanes;

    public ExecuteCodeTool(Executor mainExec, ParallelExecutor parallelExec, Executor renderExec, string game, string imports)
    {
        lanes = renderExec == null
            ? [("main", mainExec, MainProse), ("parallel", parallelExec, ParallelProse)]
            : [("main", mainExec, MainProse), ("render", renderExec, RenderProse), ("parallel", parallelExec, ParallelProse)];

        // Each field says what goes in it, and that split is what routes a script's parts — so no "do
        // NOT" lines: a using directive or a type declaration put in `code` fails to compile there, and
        // a usings item written with its own `using` or `;` doubles them; either way the error names the
        // field to fix. The usings examples show the bare form; the last one is an extern alias, which
        // the template declares for every clashing assembly (ScriptReferences, Compiler.InitShared).
        SchemaJson = ToolSchema.Build(Name,
            $"Run C# inside the running {game} game; returns what the script writes to Console. "
            + "Internal and protected types and members are accessible directly; private ones need reflection. "
            + imports,
            new ToolSchema.Param("code",
                "Statements of the script's entry method. To wait: `await` on \"parallel\", `yield return null` (next frame) elsewhere.",
                required: true),
            new ToolSchema.Param("class_body",
                "Members of the script's class, visible to `code`: methods, fields, nested types, [DllImport] externs."),
            new ToolSchema.Param("usings",
                "Extra namespaces to import, e.g. \"System.Threading.Tasks\", \"IO = System.IO\", \"static System.Math\", "
                + "or \"Foo_Bar::Ns\" for a clashing assembly Foo.Bar.",
                type: "array"),
            new ToolSchema.Param("target",
                "Thread the script runs on: " + string.Join(", ", lanes.Select(l => l.Prose)) + ".",
                @enum: [.. lanes.Select(l => l.Name)], @default: lanes[0].Name));
    }

    // What each lane IS; which lanes a host has, and which is the default, are the schema's enum and
    // default. Left out of the prose on purpose, kept here for maintainers:
    //   - main is pumped by a Postfix on the main thread's frame (MySandboxGame.Update on SE1,
    //     VRageCore.Update on SE2); render by one on the render thread's (MyRenderThread.RenderFrame,
    //     Render12EngineComponent.RenderFrame) — all at int.MinValue, not Priority.Last: Last is
    //     only 0, and any negative priority would still run after it.
    //   - render exists to inspect what runs on that thread: other plugins' Harmony hooks there, their
    //     __instance, captured locals, accumulated fields. Game and session APIs assert-throw on it
    //     (SE1's MyAPIGateway, SE2's session and scene).
    //   - both frame lanes hold up their thread for each step, under a 1 s budget per frame shared by
    //     that lane's scripts; when it runs out, the report says to split the work with
    //     `yield return null`.
    //   - parallel's entry is an async iterator: `await` compiles there and on no other lane, and
    //     `yield return null` resumes at once. Whatever a script awaits, it resumes on its own
    //     thread (ScriptPump, ScriptBuilders.cs).
    private const string MainProse = "\"main\" (game thread)";
    private const string RenderProse = "\"render\" (render thread)";
    private const string ParallelProse =
        "\"parallel\" (a thread of its own, concurrent with the game, no frame budget; for computation and blocking I/O)";

    public bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string errorMessage)
    {
        errorCode = 0;
        errorMessage = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
        {
            errorCode = -32602;
            errorMessage = "Invalid params: arguments.code must be a string";
            return false;
        }

        item.Code = codeEl.GetString();

        // class_body: optional string. JSON null treated as absent, same rule as target.
        if (arguments.TryGetProperty("class_body", out var cbEl) && cbEl.ValueKind != JsonValueKind.Null)
        {
            if (cbEl.ValueKind != JsonValueKind.String)
            {
                errorCode = -32602;
                errorMessage = "Invalid params: class_body must be a string";
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
                errorMessage = "Invalid params: usings must be an array of strings";
                return false;
            }
            var usings = new List<string>(uEl.GetArrayLength());
            foreach (var el in uEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                {
                    errorCode = -32602;
                    errorMessage = "Invalid params: usings items must be strings";
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
                errorMessage = "Invalid params: target must be a string";
                return false;
            }
            target = tEl.GetString();
        }

        var lane = string.IsNullOrEmpty(target) ? lanes[0] : Array.Find(lanes, l => l.Name == target);
        if (lane.Lane == null)
        {
            errorCode = -32602;
            errorMessage = $"Invalid params: target must be one of {string.Join(", ", lanes.Select(l => $"\"{l.Name}\""))} (got \"{target}\")";
            return false;
        }

        if (!lane.Lane.Initialized)
        {
            errorCode = -32002;
            errorMessage = NotReady(lane.Name);
            return false;
        }

        lane.Lane.Enqueue(item);
        return true;
    }

    // Points at the next call, not at the cause: another lane if one is up, a retry if none is. Main
    // and parallel open on the first update once the game has loaded; render on its own thread's
    // first frame after that — or never, when its pump doesn't run (SE1's StartSync mode, a hook
    // broken by a game update), where "retry" alone would loop for good.
    private string NotReady(string lane)
    {
        var ready = lanes.Where(l => l.Lane.Initialized).Select(l => $"\"{l.Name}\"").ToArray();
        return ready.Length == 0
            ? "Game still loading — retry shortly."
            : $"\"{lane}\" lane not ready. Ready targets: {string.Join(", ", ready)}.";
    }
}
