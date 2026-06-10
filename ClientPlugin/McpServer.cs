using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using VRage.Utils;

namespace ClientPlugin;

public sealed class McpServer : IDisposable
{
    private const int MaxPortRetries = 10;

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    private readonly Executor mainExec;
    private readonly Executor renderExec;
    private readonly int basePort;
    private HttpListener listener;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new();

    public McpServer(Executor mainExec, Executor renderExec, string port)
    {
        this.mainExec = mainExec;
        this.renderExec = renderExec;
        basePort = int.TryParse(port, out var p) ? p : 9876;
    }

    public void Start()
    {
        for (var attempt = 0; attempt < MaxPortRetries; attempt++)
        {
            var port = basePort + attempt;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                Config.Current.BoundPort = port;
                Task.Run(ListenLoop);
                MyLog.Default.WriteLine($"SeMcp: listening on :{port}");
                return;
            }
            catch (HttpListenerException)
            {
                MyLog.Default.Warning($"SeMcp: port {port} in use, trying next");
                // ReSharper disable once EmptyGeneralCatchClause
                try { listener.Close(); } catch { }
            }
            catch (Exception ex)
            {
                Config.Current.Error = ex.Message;
                MyLog.Default.Error($"SeMcp: {ex.Message}");
                return;
            }
        }

        Config.Current.Error = $"ports {basePort}-{basePort + MaxPortRetries - 1} all in use";
        MyLog.Default.Error($"SeMcp: {Config.Current.Error}");
    }

    public void Dispose()
    {
        try
        {
            listener?.Stop();
        }
        catch (Exception ex)
        {
            MyLog.Default.Error($"SeMcp: error stopping listener: {ex.Message}");
        }
    }

    private async Task ListenLoop()
    {
        while (true)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                _ = HandleRequest(ctx);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                MyLog.Default.Error($"SeMcp: listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var rawId = "null";
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                await Respond(ctx, 405, "POST only");
                return;
            }

            // Browsers always send Origin and forbid JS from forging it; native MCP clients don't. Reject browser-origin (CSRF) requests.
            if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"]))
            {
                await Respond(ctx, 403, "Forbidden: browser origin not allowed");
                return;
            }

            // Managed server don't always check host
            if (ctx.Request.Headers["Host"] != $"localhost:{Config.Current.BoundPort}")
            {
                await Respond(ctx, 403, "Forbidden: invalid host");
                return;
            }

            var h = ctx.Request.Headers["Authorization"];
            var q = ctx.Request.QueryString["token"];

            if (h != null && q != null)
            {
                await Respond(ctx, 400, "invalid_request");
                return;
            }

            var bearer = h?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? h.Substring(7) : null;
            if (!ConstantTimeEquals(bearer ?? q, Config.Current.SecretKey))
            {
                await Respond(ctx, 401, "Unauthorized");
                return;
            }

            // Session id namespaces the pending-request keys: official SDK clients all
            // count JSON-RPC ids up from 0, so two clients (or one client restarted while
            // an old script is still running) collide on ids as the norm, not the edge case.
            // Required on every request except initialize (spec: 400 without it), but the
            // VALUE is deliberately not validated against a table of issued ids — the
            // spec's 404-on-unknown-session rule exists to force re-initialization when
            // per-session server state is lost, and this server keeps none (responses pair
            // via the HTTP context, the initialize result is a static constant, auth is
            // the per-request Bearer token). Accepting any non-empty value means clients
            // keep working across a plugin or game restart without a forced re-init.
            // REVISIT if per-session state is ever added (SSE push, subscriptions,
            // list_changed notifications) — at that point unknown ids must 404.
            var sessionId = ctx.Request.Headers["Mcp-Session-Id"];

            // No body size cap: auth grants execute_code (full RCE), so capping body size here would be security theater.
            using var doc = await JsonDocument.ParseAsync(ctx.Request.InputStream);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                await RespondJsonRpcError(ctx, "null", -32600,
                    "Invalid Request: expected a single JSON-RPC request object (batching not supported)");
                return;
            }

            if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            {
                await RespondJsonRpcError(ctx, "null", -32600,
                    "Invalid Request: method must be a string");
                return;
            }

            var method = methodEl.GetString()!; // ValueKind == String guarantees non-null
            root.TryGetProperty("id", out var idEl);
            var isNotification = method.StartsWith("notifications/");

            if (method != "initialize" && string.IsNullOrEmpty(sessionId))
            {
                await Respond(ctx, 400, "Bad Request: Mcp-Session-Id header required (initialize first)");
                return;
            }

            if (!isNotification && idEl.ValueKind != JsonValueKind.String
                                && idEl.ValueKind != JsonValueKind.Number)
            {
                await RespondJsonRpcError(ctx, "null", -32600,
                    "Invalid Request: id must be a non-null string or number");
                return;
            }

            rawId = isNotification ? "null" : idEl.GetRawText();

            switch (method)
            {
                case "initialize":
                    // Mint a fresh session id on every initialize; spec-compliant clients
                    // MUST echo it on all subsequent requests. Headers must be set before
                    // the body write below.
                    ctx.Response.AddHeader("Mcp-Session-Id", Config.GenerateToken());
                    await RespondJsonRpc(ctx, rawId, JsonInitResult());
                    break;

                case "notifications/initialized":
                    // MCP Streamable HTTP: accepted notifications get 202 with no body.
                    await Respond(ctx, 202, "");
                    break;

                case "tools/list":
                    await RespondJsonRpc(ctx, rawId, JsonToolsList());
                    break;

                case "tools/call":
                    await HandleToolsCall(ctx, root, rawId, sessionId);
                    break;

                case "notifications/cancelled":
                    HandleCancel(root, sessionId);
                    await Respond(ctx, 202, "");
                    break;

                default:
                    await RespondJsonRpcError(ctx, rawId, -32601, $"Unknown method: {method}");
                    break;
            }
        }
        catch (JsonException)
        {
            await RespondJsonRpcError(ctx, "null", -32700, "Parse error");
        }
        catch (Exception ex)
        {
            MyLog.Default.Error($"SeMcp: request error: {ex}");
            await RespondJsonRpcError(ctx, rawId, -32603, "Internal error");
        }
    }

    private async Task HandleToolsCall(HttpListenerContext ctx, JsonElement root, string rawId, string sessionId)
    {
        // Shape checks return -32602 with a precise message; the generic catch in
        // HandleRequest would mislabel them all as -32603 Internal error.
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            await RespondJsonRpcError(ctx, rawId, -32602, "Invalid params: name must be a string");
            return;
        }

        var toolName = nameEl.GetString();
        if (toolName != "execute_code")
        {
            await RespondJsonRpcError(ctx, rawId, -32602, $"Unknown tool: {toolName}");
            return;
        }

        if (!p.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
        {
            await RespondJsonRpcError(ctx, rawId, -32602, "Invalid params: arguments.code must be a string");
            return;
        }

        var code = codeEl.GetString();

        // target routes to one of the two execution lanes. Default "main" preserves
        // pre-render-thread behavior. The string never enters WorkItem / Executor —
        // it's resolved to a concrete Executor instance here and discarded.
        // JSON null is treated as absent; any other non-string shape is rejected
        // rather than silently falling back to "main".
        string target = null;
        if (args.TryGetProperty("target", out var tEl) && tEl.ValueKind != JsonValueKind.Null)
        {
            if (tEl.ValueKind != JsonValueKind.String)
            {
                await RespondJsonRpcError(ctx, rawId, -32602, "Invalid params: target must be a string");
                return;
            }
            target = tEl.GetString();
        }

        var executor = target switch
        {
            null or "" or "main" => mainExec,
            "render" => renderExec,
            _ => null
        };
        if (executor == null)
        {
            await RespondJsonRpcError(ctx, rawId, -32602,
                $"Invalid params: target must be \"main\" or \"render\" (got \"{target}\")");
            return;
        }

        if (!executor.Initialized)
        {
            await RespondJsonRpcError(ctx, rawId, -32002,
                "Game is still loading, not all plugins have been initialized yet. Please retry shortly.");
            return;
        }

        var cancelSource = new CancellationTokenSource();
        var item = new WorkItem { Code = code, Cancel = cancelSource.Token };

        // MCP requires id uniqueness per session; refuse rather than silently orphan the
        // prior request. The session prefix scopes that uniqueness correctly: official
        // SDK clients all count ids up from 0, so cross-client raw-id collisions are
        // routine and must not be conflated.
        var pendingKey = sessionId + ":" + rawId;
        if (!pending.TryAdd(pendingKey, cancelSource))
        {
            cancelSource.Dispose();
            await RespondJsonRpcError(ctx, rawId, -32600,
                "Invalid Request: request id already in-flight in this session");
            return;
        }
        executor.Enqueue(item);

        await item.Done.Task;
        pending.TryRemove(pendingKey, out _);
        cancelSource.Dispose();

        var isError = item.Error != null || item.WasCancelled;
        var text = item.WasCancelled
            ? $"[cancelled]\n{item.Output}"
            : item.Error != null
                ? $"{item.Output}\n\n{item.Error}"
                : item.Output;

        try { await RespondJsonRpc(ctx, rawId, JsonToolResult(text.Trim(), isError)); }
        catch (Exception ex) { MyLog.Default.Warning($"SeMcp: response flush failed (listener likely closed): {ex.Message}"); }
    }

    private void HandleCancel(JsonElement root, string sessionId)
    {
        // MCP spec: invalid cancellation notifications SHOULD be silently ignored.
        // Session-scoped lookup: without the prefix, a client cancelling its own id=2
        // could kill another client's in-flight id=2.
        try
        {
            if (!root.TryGetProperty("params", out var p)) return;
            if (!p.TryGetProperty("requestId", out var rid)) return;
            if (pending.TryGetValue(sessionId + ":" + rid.GetRawText(), out var cancelSource))
                cancelSource.Cancel();
        }
        catch (ObjectDisposedException) { /* race with HandleToolsCall.Dispose() */ }
    }

    private static string JsonInitResult()
    {
        return """{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"SeMcp","version":"1.0.0"}}""";
    }

    private static string JsonToolsList()
    {
        return """{"tools":[{"name":"execute_code","description":"Execute C# code inside Space Engineers. Full .NET + game API access. Use Console.WriteLine() for output. Use yield return null to pause until next frame. In multiplayer, requires Admin or Owner promote level. ALWAYS use short type names. Pre-imported namespaces: System.*, VRage.*, VRageMath, Sandbox.*, SpaceEngineers.Game.*. Do NOT write fully qualified names like Sandbox.Game.World.MySession.Static or Sandbox.Game.Entities.MyCubeGrid — write MySession.Static, MyCubeGrid. Extra 'using' directives at the top of your code are supported. Only fall back to fully qualified names on ambiguous type errors.","inputSchema":{"type":"object","properties":{"code":{"type":"string","description":"C# code body. Using directives at the top are supported; no class/method wrapper needed."},"target":{"type":"string","enum":["main","render"],"description":"Execution lane. \"main\" (default) runs in the game's main thread via IPlugin.Update — use this for MyAPIGateway / Session / Grid / Entity access. \"render\" runs in the render thread via a Harmony Postfix on MyRenderThread.RenderFrame — use ONLY to inspect other plugins' Harmony hooks that execute on the render thread (their __instance, captured locals, accumulated fields). Render-target scripts freeze one frame per step (~16ms); use yield return null to split work across frames. MyAPIGateway will assert-throw on render thread."}},"required":["code"]}}]}""";
    }

    private static string JsonToolResult(string text, bool isError)
    {
        var escaped = JsonEncodedText.Encode(text);
        return $$"""{"content":[{"type":"text","text":"{{escaped}}"}],"isError":{{(isError ? "true" : "false")}}}""";
    }

    private static async Task RespondJsonRpc(HttpListenerContext ctx, string rawId, string resultJson)
    {
        var json = $$"""{"jsonrpc":"2.0","id":{{rawId}},"result":{{resultJson}}}""";
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        using var w = new StreamWriter(ctx.Response.OutputStream, Utf8NoBom);
        await w.WriteAsync(json);
    }

    private static async Task RespondJsonRpcError(HttpListenerContext ctx, string rawId, int code, string message)
    {
        var escaped = JsonEncodedText.Encode(message);
        var json = $$$"""{"jsonrpc":"2.0","id":{{{rawId}}},"error":{"code":{{{code}}},"message":"{{{escaped}}}"}}""";
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        using var w = new StreamWriter(ctx.Response.OutputStream, Utf8NoBom);
        await w.WriteAsync(json);
    }

    private static async Task Respond(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        using var w = new StreamWriter(ctx.Response.OutputStream, Utf8NoBom);
        await w.WriteAsync(body);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
