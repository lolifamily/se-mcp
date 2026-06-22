# SeMcp

Turn a running **Space Engineers** game — or a dedicated server — into an
[MCP](https://modelcontextprotocol.io) server. An LLM connects over local HTTP
and executes **C# directly inside the live engine**, with full .NET and game API
access.

---

> ## ⚠️ This is remote code execution by design
>
> The bearer token **is** the root password. Anyone holding it can run arbitrary
> C# in the game process: file I/O, spawning processes, native `[DllImport]` —
> the lot. There is no sandbox.
>
> - **Never** share, screenshot, or commit the token.
> - **Never** port-forward the listener or expose it past `localhost`.
> - The server binds `127.0.0.1` only, rejects browser-origin (CSRF) and
>   foreign `Host` headers, and uses constant-time token comparison. In
>   multiplayer the client additionally requires **Admin/Owner** promote level.
> - The per-script watchdog (1 s/frame, 700 KB stack) exists to stop runaway
>   loops from hanging the game thread. **It is not a security boundary** — a
>   token holder already has full RCE.

---

## What it is

```
        MCP client  (Claude, etc.)
              │   HTTP · JSON-RPC · Bearer token   (127.0.0.1 only)
              ▼
       ┌──────────────────────────────┐
       │  McpServer        (Shared)    │  HttpListener · JSON-RPC · auth · sessions
       │  ITool dispatch               │
       │  Executor · Compiler · Guard  │  Roslyn compile → Cecil IL guard → coroutine
       └──────────────────────────────┘
              │  runs your C# on a game thread
        ┌─────┴───────────────┐
        ▼                     ▼
    main lane             render lane                ← client only
    IPlugin.Update        Harmony postfix on
    (game / API state)    MyRenderThread.RenderFrame
```

Two hosts share one MCP core (`Shared`): **`ClientPlugin`** (loaded by Pulsar,
in-game) and **`ServerPlugin`** (loaded by Magnetar, dedicated server). Code runs
on the game's **main** thread (`IPlugin.Update`) or, on the client, on the
**render** thread (a Harmony postfix on `MyRenderThread.RenderFrame`).

## Connecting

On first launch the plugin auto-generates a token. Where to find it and the URL:

- **Client** — open the in-game settings dialog and click **Copy URL**. You get
  `http://localhost:9876/?token=<token>` on the clipboard. (Default port
  `9876`; if taken it climbs `9876→9885` — the live port shows in the dialog
  title and the log line `listening on :<port>`.)
- **Server** — no GUI. The token lives in `<UserDataPath>/SeMcp.cfg`. Default
  port `9000`; same `9000→9009` climb if taken, with the bound port in the log.

> Use `localhost`, **not** `127.0.0.1` — the `Host` header is checked and a
> mismatch returns `403`.

Transport is **MCP Streamable HTTP** (not SSE): POST JSON-RPC to
`http://localhost:<port>/`. Auth is either an `Authorization: Bearer <token>`
header **or** a `?token=<token>` query parameter (not both). Point any standard
MCP client at it — it will `initialize`, pick up the `Mcp-Session-Id`, and manage
the session for you:

```json
{
  "mcpServers": {
    "se-mcp": {
      "type": "streamable-http",
      "url": "http://localhost:9876/",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Driving the raw protocol by hand (note: every call after `initialize` must echo
the session id):

```bash
# 1. initialize — the Mcp-Session-Id comes back in the response headers
curl -i http://localhost:9876/ \
  -H 'Authorization: Bearer <token>' -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'

# 2. call a tool
curl http://localhost:9876/ \
  -H 'Authorization: Bearer <token>' -H 'Mcp-Session-Id: <id>' \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call",
       "params":{"name":"execute_code",
                 "arguments":{"code":"Console.WriteLine(MySession.Static?.Name);"}}}'
```

## `execute_code`

Your input maps 1:1 onto three C# layers, which are spliced into a wrapper:

```csharp
// <usings>            ← extra "using" lines (defaults already imported)
public class __REPL__
{
    // <class_body>     ← methods, fields, nested types, [DllImport] — class-level
    public IEnumerable<object> Run(TextWriter Console)
    {
        // <code>       ← statements only; this is the entry point
        yield break;
    }
}
```

| field        | required | what goes in it                                                                                             |
|--------------|----------|-------------------------------------------------------------------------------------------------------------|
| `code`       | yes      | **Statements only.** Output via `Console.WriteLine()`. Pause until the next frame with `yield return null`. |
| `class_body` | no       | Class-level declarations — anything that can't live in a method body, e.g. `[DllImport]` P/Invoke.          |
| `usings`     | no       | Extra namespace imports — bare paths like `"System.Runtime.InteropServices"`, no `using` keyword, no `;`.   |
| `target`     | no       | `"main"` (default) or `"render"` (client only).                                                             |

- A large set of namespaces (`System.*`, `VRageMath`, `VRage.*`, `Sandbox.*`,
  `SpaceEngineers.Game.*`, …) is **pre-imported**. Use short type names
  (`MySession.Static`, `MyCubeGrid`), not fully-qualified ones.
- Compiled with **Roslyn 5.0** against **every loaded assembly** (.NET + game +
  other plugins). `unsafe` and `[DllImport]` are allowed.
- Compile errors come back per field with corrected line numbers:
  `code (3,9): error CS0103: ...`.
- Scripts are coroutines and **run in parallel**; each step is bounded by the
  watchdog above.

**Examples**

Read game state on the main thread:

```csharp
var s = MyAPIGateway.Session;
Console.WriteLine($"World: {s.Name}");
Console.WriteLine($"You are at: {s.Player?.GetPosition()}");
```

P/Invoke via `class_body` + `usings` (`usings: ["System.Runtime.InteropServices"]`):

```csharp
// class_body
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

// code
MessageBox(IntPtr.Zero, "Hello from Space Engineers", "SeMcp", 0);
Console.WriteLine("shown");
```

Spread work across frames:

```csharp
for (int i = 3; i > 0; i--)
{
    Console.WriteLine($"tick {i}");
    yield return null;   // resume next frame
}
Console.WriteLine("done");
```

> The `render` target runs on the render thread — use it **only** to inspect
> other plugins' Harmony hooks that execute there. `MyAPIGateway` asserts off the
> main thread.

The client also exposes **`take_screenshot`** (captures the current frame as an
image; optional `ignore_sprites` to drop the HUD). Full parameters are in the
tool's `inputSchema`.

## Client vs. server

|                   | Client (Pulsar)                    | Server (Magnetar)            |
|-------------------|------------------------------------|------------------------------|
| Default port      | `9876` (retries `9876–9885`)       | `9000` (retries `9000–9009`) |
| `render` lane     | ✅                                  | ❌                            |
| `take_screenshot` | ✅                                  | ❌                            |
| Multiplayer gate  | Admin/Owner required               | none — token is full access  |
| Settings GUI      | ✅                                  | ❌ (edit the `.cfg`)          |
| Config file       | `<UserDataPath>/Storage/SeMcp.cfg` | `<UserDataPath>/SeMcp.cfg`   |

## Configuration

`Port` and `SecretKey` persist to the `.cfg` (auto-saved). On the client both are
editable in the settings dialog, with **Regenerate Token** and **Copy URL**
buttons; the server is file-only. **Changing the port requires a restart.**

## How it works

For anyone reading or extending the code:

- **`McpServer`** — one `HttpListener`, single (non-batched) JSON-RPC, auth +
  CSRF/host checks, pre-rendered `tools/list`. Sessions namespace in-flight
  request ids but carry no server state.
- **`Executor`** — compilation runs on the thread pool; the compiled coroutine is
  then stepped one `MoveNext` per frame on its owning game thread. Each lane has
  its own executor and `ScriptGuard`, so finally-blocks and thread-affine state
  stay on the right thread.
- **`Compiler`** — drives Roslyn entirely through **reflection** (no compile-time
  binding, so it works against both the game's ancient Roslyn and the NuGet 5.0
  one), references every loaded assembly, then rewrites the emitted IL with
  **Mono.Cecil** to inject the guard.
- **`ScriptGuard`** — injected `Bail()` on backward branches and `catch`→filter
  rewrites (so a `catch` can't swallow the abort), plus a stack-depth `StackCheck`
  at call sites. A background 1 s timer sets the `Dead` flag the injected checks
  read.

## License

MIT — see [LICENSE](LICENSE).
