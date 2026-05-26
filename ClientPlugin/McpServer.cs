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

    private readonly Executor executor;
    private readonly string secretKey;
    private readonly int basePort;
    private HttpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<object, CancellationTokenSource> pending = new();
    private string sessionId;

    public McpServer(Executor executor, string port, string secretKey)
    {
        this.executor = executor;
        this.secretKey = secretKey ?? "";
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
                Task.Run(ListenLoop);
                Config.Current.Status = $"listening on :{port}";
                MyLog.Default.WriteLine($"SeMcp: {Config.Current.Status}");
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
                Config.Current.Status = $"failed: {ex.Message}";
                MyLog.Default.Error($"SeMcp: {Config.Current.Status}");
                return;
            }
        }

        Config.Current.Status = $"failed: ports {basePort}-{basePort + MaxPortRetries - 1} all in use";
        MyLog.Default.Error($"SeMcp: {Config.Current.Status}");
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            listener.Stop();
        }
        catch (Exception ex)
        {
            MyLog.Default.Error($"SeMcp: error stopping listener: {ex.Message}");
        }
    }

    private async Task ListenLoop()
    {
        while (!cts.IsCancellationRequested)
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
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                await Respond(ctx, 405, "POST only");
                return;
            }

            if (secretKey.Length > 0)
            {
                var auth = ctx.Request.Headers["Authorization"] ?? "";
                if (auth != $"Bearer {secretKey}")
                {
                    await Respond(ctx, 401, "Unauthorized");
                    return;
                }
            }

            using var reader = new StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            root.TryGetProperty("id", out var idEl);

            switch (method)
            {
                case "initialize":
                    sessionId = Guid.NewGuid().ToString();
                    ctx.Response.Headers["Mcp-Session-Id"] = sessionId;
                    await RespondJsonRpc(ctx, idEl, JsonInitResult());
                    break;

                case "notifications/initialized":
                    await Respond(ctx, 200, "");
                    break;

                case "tools/list":
                    await RespondJsonRpc(ctx, idEl, JsonToolsList());
                    break;

                case "tools/call":
                    await HandleToolsCall(ctx, root, idEl);
                    break;

                case "notifications/cancelled":
                    HandleCancel(root);
                    await Respond(ctx, 200, "");
                    break;

                default:
                    await RespondJsonRpcError(ctx, idEl, -32601, $"Unknown method: {method}");
                    break;
            }
        }
        catch (JsonException)
        {
            await Respond(ctx, 400, "Invalid JSON");
        }
        catch (Exception ex)
        {
            MyLog.Default.Error($"SeMcp: request error: {ex}");
            await Respond(ctx, 500, ex.Message);
        }
    }

    private async Task HandleToolsCall(HttpListenerContext ctx, JsonElement root, JsonElement idEl)
    {
        var p = root.GetProperty("params");
        var toolName = p.GetProperty("name").GetString();

        if (toolName != "execute_code")
        {
            await RespondJsonRpcError(ctx, idEl, -32602, $"Unknown tool: {toolName}");
            return;
        }

        var code = p.GetProperty("arguments").GetProperty("code").GetString();
        var cancelSource = new CancellationTokenSource();
        var item = new WorkItem { Code = code, Cancel = cancelSource.Token };

        pending.TryAdd(idEl.ToString(), cancelSource);
        executor.Enqueue(item);

        await item.Done.Task;
        pending.TryRemove(idEl.ToString(), out _);

        var isError = item.Error != null || item.WasCancelled;
        var text = item.WasCancelled
            ? $"[cancelled]\n{item.Output}"
            : item.Error != null
                ? $"{item.Output}\n[error]\n{item.Error}"
                : item.Output;

        var result = JsonToolResult(text.Trim(), isError);
        ctx.Response.Headers["Mcp-Session-Id"] = sessionId;
        await RespondJsonRpc(ctx, idEl, result);
    }

    private void HandleCancel(JsonElement root)
    {
        var p = root.GetProperty("params");
        var reqId = p.GetProperty("requestId").ToString();
        if (pending.TryGetValue(reqId, out var cancelSource))
            cancelSource.Cancel();
    }

    private static string JsonInitResult()
    {
        return """{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"SeMcp","version":"1.0.0"}}""";
    }

    private static string JsonToolsList()
    {
        return """{"tools":[{"name":"execute_code","description":"Execute C# code inside Space Engineers. Full .NET + game API access. Use Console.WriteLine() for output. Use yield return null to pause until next frame. In multiplayer, requires Admin or Owner promote level. Pre-imported namespaces: System.*, VRage.*, VRageMath, Sandbox.*, SpaceEngineers.Game.* — use short type names (e.g. MySession.Static, MyEntities, MyCubeGrid). Extra 'using' directives at the top of your code are supported. Only use fully qualified names when you get an ambiguous type error.","inputSchema":{"type":"object","properties":{"code":{"type":"string","description":"C# code body. Using directives at the top are supported; no class/method wrapper needed."}},"required":["code"]}}]}""";
    }

    private static string JsonToolResult(string text, bool isError)
    {
        var escaped = JsonEncodedText.Encode(text);
        return $$"""{"content":[{"type":"text","text":"{{escaped}}"}],"isError":{{(isError ? "true" : "false")}}}""";
    }

    private static async Task RespondJsonRpc(HttpListenerContext ctx, JsonElement id, string resultJson)
    {
        var json = $$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{resultJson}}}""";
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        using var w = new StreamWriter(ctx.Response.OutputStream);
        await w.WriteAsync(json);
    }

    private static async Task RespondJsonRpcError(HttpListenerContext ctx, JsonElement id, int code, string message)
    {
        var escaped = JsonEncodedText.Encode(message);
        var json = $$$"""{"jsonrpc":"2.0","id":{{{id.GetRawText()}}},"error":{"code":{{{code}}},"message":"{{{escaped}}}"}}""";
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        using var w = new StreamWriter(ctx.Response.OutputStream);
        await w.WriteAsync(json);
    }

    private static async Task Respond(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        using var w = new StreamWriter(ctx.Response.OutputStream);
        await w.WriteAsync(body);
    }
}
