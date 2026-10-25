using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Shared.Config;
using Shared.Plugin;

namespace Shared.Mcp;

// The MCP endpoint: Streamable HTTP on localhost, one JSON-RPC request per POST, each carrying the
// token (SecretKey). No batches, no SSE. A tools/call holds its response until the tool answers,
// writing a space every second meanwhile; a write that fails means the client hung up.
public sealed class McpServer : IDisposable
{
    private const int MaxPortRetries = 10;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly byte[] Space = [(byte)' '];

    private readonly IPluginConfig config;
    private readonly Dictionary<string, ITool> tools;
    // The tools/list and initialize results, rendered once: nothing in them changes after startup.
    private readonly string toolsListJson;
    private readonly string initResultJson;
    private readonly int basePort;
    private HttpListener listener;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> pending = new();

    public McpServer(IReadOnlyList<ITool> tools, IPluginConfig config, string game, bool dedicated)
    {
        this.config = config;
        this.tools = new Dictionary<string, ITool>(tools.Count);
        var sb = new StringBuilder("""{"tools":[""");
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            this.tools[t.Name] = t;
            if (i > 0) sb.Append(',');
            sb.Append(t.SchemaJson);
        }
        sb.Append("]}");
        toolsListJson = sb.ToString();

        // Launch constants only: a client reads this once per session.
        var instructions = JsonEncodedText.Encode(
            $"{game} on {RuntimeInformation.FrameworkDescription}, {(dedicated ? "dedicated server" : "physical client")}.");
        // AssemblyVersion is the only one MSBuild and Pulsar builds emit alike.
        var version = typeof(McpServer).Assembly.GetName().Version;
        initResultJson = $$$"""{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"SeMcp","version":"{{{version}}}"},"instructions":"{{{instructions}}}"}""";

        basePort = int.TryParse(config.Port, out var p) ? p : 9876;
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
                config.BoundPort = port;
                Task.Run(ListenLoop);
                Common.Logger.Info($"listening on :{port}");
                return;
            }
            catch (HttpListenerException)
            {
                Common.Logger.Warning($"port {port} in use, trying next");
                // ReSharper disable once EmptyGeneralCatchClause
                try { listener.Close(); } catch { }
            }
            catch (Exception ex)
            {
                config.ErrorMessage = ex.Message;
                Common.Logger.Error(ex.Message);
                return;
            }
        }

        config.ErrorMessage = $"ports {basePort}-{basePort + MaxPortRetries - 1} all in use";
        Common.Logger.Error(config.ErrorMessage);
    }

    public void Dispose()
    {
        try
        {
            listener?.Stop();
        }
        catch (Exception ex)
        {
            Common.Logger.Error($"error stopping listener: {ex.Message}");
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
                Common.Logger.Error($"listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var rawId = "null";
        try
        {
            // The listener takes every interface (http.sys, Wine) and Host is forgeable; a source address
            // isn't. Not LocalEndPoint: from outside, a packet can be addressed to 127.0.0.1.
            if (!IsLoopbackPeer(ctx.Request.RemoteEndPoint))
            {
                await Respond(ctx, 403, "Forbidden: localhost only");
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                await Respond(ctx, 405, "POST only");
                return;
            }

            // Browsers always send Origin and don't let a page forge it; native MCP clients send none.
            // So a request with one comes from a web page: CSRF.
            if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"]))
            {
                await Respond(ctx, 403, "Forbidden: browser origin not allowed");
                return;
            }

            // Only Windows' http.sys guarantees Host matches the prefix; the managed implementation,
            // which every other platform uses, doesn't.
            if (ctx.Request.Headers["Host"] != $"localhost:{config.BoundPort}")
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

            var bearer = h?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? h.Remove(0, 7) : null;
            // Not 401: that makes MCP clients probe for OAuth, pointless with a static token.
            if (!ConstantTimeEquals(bearer ?? q, config.SecretKey))
            {
                await Respond(ctx, 403, "Forbidden: invalid token");
                return;
            }

            // Namespaces the pending-request keys: the official SDK clients all count ids up from 0, so
            // two clients, or a restarted one and a script still running from before, share ids as a
            // rule. Required on all but initialize (spec: 400), but not checked against the ids issued:
            // the spec's 404 for an unknown session forces a re-initialize after per-session state is
            // lost, and this server keeps none (each answer goes back on its own connection, the
            // initialize result is fixed, the token comes with every request), so clients carry on
            // across a plugin or game restart. Once there is per-session state (SSE, subscriptions,
            // list_changed), unknown ids must get that 404.
            var sessionId = ctx.Request.Headers["Mcp-Session-Id"];

            // No body size cap: a request that got here holds the token, which runs any code anyway.
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
            var isNotification = method.StartsWith("notifications/", StringComparison.Ordinal);

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
                    // A fresh session id per initialize, which the spec has the client send back on
                    // every later request. Set before the body is written, which sends the headers.
                    ctx.Response.AddHeader("Mcp-Session-Id", TokenGenerator.Generate());
                    await RespondJsonRpc(ctx, rawId, initResultJson);
                    break;

                case "notifications/initialized":
                    // MCP Streamable HTTP: accepted notifications get 202 with no body.
                    await Respond(ctx, 202, "");
                    break;

                case "ping":
                    await RespondJsonRpc(ctx, rawId, "{}");
                    break;

                case "tools/list":
                    await RespondJsonRpc(ctx, rawId, toolsListJson);
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
            Common.Logger.Error(ex, "request error");
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
        if (toolName == null || !tools.TryGetValue(toolName, out var tool))
        {
            await RespondJsonRpcError(ctx, rawId, -32602, $"Unknown tool: {toolName}");
            return;
        }

        // Absent for a call with no arguments: `args` is then default(JsonElement), ValueKind
        // Undefined, and every tool checks for an object before reading it.
        _ = p.TryGetProperty("arguments", out var args);

        // Ids are unique per session (MCP), so a duplicate is refused rather than left to orphan the
        // first. Claimed before TryDispatch: dispatching is the side effect a refused duplicate must
        // not have.
        var pendingKey = sessionId + ":" + rawId;
        using var cancelSource = new CancellationTokenSource();
        if (!pending.TryAdd(pendingKey, cancelSource))
        {
            await RespondJsonRpcError(ctx, rawId, -32600,
                "Invalid Request: request id already in-flight in this session");
            return;
        }

        var item = new WorkItem { Cancel = cancelSource.Token };
        try
        {
            if (!tool.TryDispatch(args, item, out var errorCode, out var error))
            {
                await RespondJsonRpcError(ctx, rawId, errorCode, error);
                return;
            }

            // Only a failed write reveals a client that hung up; leading spaces are valid JSON.
            // The first space sends the headers, so the content type has to be set before it.
            ctx.Response.ContentType = "application/json";
            while (await Task.WhenAny(item.Done.Task, Task.Delay(1000)) != item.Done.Task)
            {
                try { await ctx.Response.OutputStream.WriteAsync(Space, 0, 1); }
                catch { cancelSource.Cancel(); ctx.Response.Abort(); return; }
            }
        }
        finally
        {
            pending.TryRemove(pendingKey, out _);
        }

        // On success a screenshot's Output is the saved file's path, read and encoded here, off the
        // game's threads (WorkItem.Done); a failed read reaches HandleRequest's catch as -32603. A text
        // result is the output, then how the run ended, a cancel included (ScriptRender.Combine).
        var isError = item.Error != null || item.WasCancelled;
        var tail = item.WasCancelled ? "(cancelled)" : item.Error;
        var resultJson = tool.ReturnsImage && !isError
            ? JsonToolResultImage(File.ReadAllBytes(item.Output), item.Output)
            : JsonToolResult(ScriptRender.Combine(item.Output, tail), isError);

        try { await RespondJsonRpc(ctx, rawId, resultJson); }
        catch (Exception ex) { Common.Logger.Warning($"response flush failed (listener likely closed): {ex.Message}"); }
    }

    private void HandleCancel(JsonElement root, string sessionId)
    {
        // An invalid cancellation is ignored, as MCP asks. Looked up within the session: a client
        // cancelling its id 2 must not kill another client's.
        try
        {
            if (!root.TryGetProperty("params", out var p)) return;
            if (!p.TryGetProperty("requestId", out var rid)) return;
            if (pending.TryGetValue(sessionId + ":" + rid.GetRawText(), out var cancelSource))
                cancelSource.Cancel();
        }
        catch (ObjectDisposedException) { /* HandleToolsCall disposed it in the meantime */ }
    }

    private static string JsonToolResult(string text, bool isError)
    {
        var escaped = JsonEncodedText.Encode(text);
        return $$"""{"content":[{"type":"text","text":"{{escaped}}"}],"isError":{{(isError ? "true" : "false")}}}""";
    }

    private static string JsonToolResultImage(byte[] data, string savedPath)
    {
        // base64 needs no escaping, only the path does. The text block gives the file's location, so
        // the model can point the user at it: the folder doubles as a screenshot history.
        var b64 = Convert.ToBase64String(data);
        var note = JsonEncodedText.Encode("saved to " + savedPath);
        return $$"""{"content":[{"type":"image","data":"{{b64}}","mimeType":"image/jpeg"},{"type":"text","text":"{{note}}"}],"isError":false}""";
    }

    private static Task RespondJsonRpc(HttpListenerContext ctx, string rawId, string resultJson) =>
        Send(ctx, 200, "application/json", $$"""{"jsonrpc":"2.0","id":{{rawId}},"result":{{resultJson}}}""");

    private static Task RespondJsonRpcError(HttpListenerContext ctx, string rawId, int code, string message)
    {
        var escaped = JsonEncodedText.Encode(message);
        return Send(ctx, 200, "application/json",
            $$$"""{"jsonrpc":"2.0","id":{{{rawId}}},"error":{"code":{{{code}}},"message":"{{{escaped}}}"}}""");
    }

    private static Task Respond(HttpListenerContext ctx, int status, string body) =>
        Send(ctx, status, "text/plain", body);

    // The whole body in one async write, then the stream closed. Not a StreamWriter: disposing
    // one flushes its tail synchronously, and net48's has no DisposeAsync to do it otherwise.
    // After a heartbeat the headers are already out; HttpListener then ignores the status and
    // content type set here rather than refusing them.
    private static async Task Send(HttpListenerContext ctx, int status, string contentType, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        var bytes = Utf8NoBom.GetBytes(body);
        var stream = ctx.Response.OutputStream;
        await stream.WriteAsync(bytes, 0, bytes.Length);
        stream.Close();
    }

    // ::ffff:127.x is a dual-mode socket's IPv4 peer, which IsLoopback misses on .NET Framework (and
    // past ::ffff:127.0.0.1 on .NET). No address, no service.
    private static bool IsLoopbackPeer(IPEndPoint peer)
    {
        var address = peer?.Address;
        if (address == null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    // Its time doesn't depend on where the two strings differ. Hand-rolled, as net48 has no
    // CryptographicOperations.FixedTimeEquals.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
