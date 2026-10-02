# SeMcp

Turn a running **Space Engineers** game into an
[MCP](https://modelcontextprotocol.io) server — **SE1 (client or dedicated
server) and SE2 (client)**. An LLM connects over local HTTP and executes **C#
directly inside the live engine**, with full .NET and game API access — right
down to `internal` types and members.

---

> ## ⚠️ This is remote code execution by design
>
> The bearer token **is** the root password. Anyone holding it can run arbitrary
> C# in the game process: file I/O, spawning processes, native `[DllImport]` —
> the lot. There is no sandbox.
>
> - **Never** share, screenshot, or commit the token.
> - **Never** port-forward the listener or expose it past `localhost`.
> - The server answers `http://localhost:<port>` from this machine only: the
>   port may listen on every interface, but other machines and any other
>   `Host` (`127.0.0.1` included) are refused. Browser-origin (CSRF) requests
>   are rejected and the token is compared in constant time. In multiplayer
>   the client additionally gates execution (SE1: **Admin/Owner** promote
>   level; SE2: **host / local-server** sessions only).
> - The per-script watchdog (1 s/frame, 700 KB stack) exists to stop runaway
>   loops from hanging the game thread. **It is not a security boundary** — a
>   token holder already has full RCE.

---

## What it is

```
        MCP client  (Claude, etc.)
              │   HTTP · JSON-RPC · Bearer token   (localhost only)
              ▼
       ┌──────────────────────────────┐
       │  McpServer        (Shared)    │  HttpListener · JSON-RPC · auth · sessions
       │  ITool dispatch               │
       │  Executor · Compiler · Guard  │  Roslyn compile → Cecil IL guard → coroutine
       └──────────────────────────────┘
              │  runs your C#
        ┌─────┴───────────────┬───────────────────────┐
        ▼                     ▼                       ▼
    main lane             render lane (client)    parallel lane
    per-frame pump        postfix on the render   a thread per script,
    (game / API state)    thread (hook per host)  concurrent, off-frame
```

Three hosts share one MCP core (`Shared`): SE1 **`ClientPlugin`** (Pulsar,
in-game), SE1 **`ServerPlugin`** (Magnetar, dedicated server), and SE2
**`Client2Plugin`** (Pulsar Modern, in-game — ships as `SeMcp2`). Code runs on the
game's **main** thread, on either client's **render** thread, or on a thread of its
own (**parallel**). The per-frame pump differs by host:

- **main lane** — a Harmony postfix on the main thread's frame: SE1 (client and
  server) `MySandboxGame.Update`, SE2 `VRageCore.Update`.
- **render lane** (client only) — SE1 a Harmony postfix on
  `MyRenderThread.RenderFrame`; SE2 on `Render12EngineComponent.RenderFrame`.
- Every pump postfix runs at Harmony priority `int.MinValue`, after every other
  plugin's postfix on the same method, so a script sees the frame fully settled.
- **parallel lane** — no pump: each script runs to its end on a thread of its own,
  concurrently with the game.

`execute_code` works on all three; `take_screenshot` and the render lane are
client-only.

## Connecting

On first launch the plugin auto-generates a token. Where to find it and the URL:

- **Client** — open the in-game settings dialog and click **Copy URL**. You get
  `http://localhost:9876/?token=<token>` on the clipboard. (Default port
  `9876`, or `6789` on the SE2 client; if taken it climbs `9876→9885` — the live
  port shows in the dialog title and the log line `listening on :<port>`.)
- **Server** — on Quasar-managed servers, open the **Plugin configuration** page
  to view the token and edit the port. Standalone servers without Quasar can read
  the token from `<UserDataPath>/SeMcp.cfg`. Default port `9000`; same
  `9000→9009` climb if taken, with the bound port in the log.

> Use `localhost`, **not** `127.0.0.1` — the `Host` header is checked and a
> mismatch is refused.

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
    static TextWriter Console => …;   // your output — from code, class_body, nested types, any thread
    // <class_body>     ← methods, fields, nested types, [DllImport] — class-level
    public IEnumerable<object> Run()
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
| `target`     | no       | `"main"` (default), `"render"` (client only) or `"parallel"`.                                               |

- A large set of namespaces is **pre-imported** — SE1: `System.*`, `VRageMath`,
  `VRage.*`, `Sandbox.*`, `SpaceEngineers.Game.*`; SE2: `System.*`,
  `Keen.VRage.*`, `Keen.Game2.*`. Use short type names (SE1 `MySession.Static`,
  SE2 `GameAppComponent`), not fully-qualified ones.
- Compiled with **Roslyn 5.3** against **every loaded assembly** (.NET + game +
  other plugins) **and every one the game can still load**, each at the file the
  runtime will load it from, with **ignore-accessibility** turned on: `internal`
  classes, methods, fields and properties are callable **directly, no reflection**
  — this works across the game's own assemblies *and* other loaded plugins (only
  truly `private` members still need reflection). `unsafe` and `[DllImport]` are
  allowed. An assembly not loaded yet that would change what a name already means
  in a script is reached through an extern alias instead, its name with `_` for
  `.`: `Foo_Bar::Namespace.Type`.
- Compile errors come back per field with corrected line numbers:
  `code(3,9): error CS0103: ...`.
- Scripts are coroutines and **run in parallel**; each step is bounded by the
  watchdog above. The 1 s is **one budget per frame, shared** by every script
  stepped in it: the script running when it runs out is killed (the report says
  how long its own step took, so a bystander can be told from the culprit), and
  scripts whose turn comes after that end with a *retry* error instead.
- Hanging up cancels the script within about 2 s, as `notifications/cancelled`
  would: while it runs, a space goes ahead of the JSON every second, and the
  first one that can't be written cancels it. Only the direct TCP peer is seen,
  so keep `localhost` in `NO_PROXY`.
- `parallel` has no frame budget: `yield return null` resumes at once. A cancel
  or hang-up answers at once and **aborts** the script's thread, so a loop with no
  check point in it stops too; code stuck in a `finally`, a `catch` or a native
  call can't be stopped.
- `await` works on `parallel`, and only there — its entry is
  `async IAsyncEnumerable<object> Run()`. Whatever a script awaits, it resumes on
  its own thread, the one a cancel aborts: even after `ConfigureAwait(false)`, and
  even when an SE2 engine task completes on the game thread. Don't block on your
  own async methods there (`.Result`, `.Wait()`): their continuations wait for the
  very thread you're blocking, until a cancel frees it. Methods returning SE2's
  own `Task` type keep Keen's rules.
- On every lane, a script's faulted `Task` that nobody awaits stays quiet (SE2
  treats an unobserved one as a crash), and an `async void` method's exception
  becomes its script's error on `parallel`, a log line elsewhere.

**Examples**

Read game state on the main thread:

```csharp
var s = MyAPIGateway.Session;
Console.WriteLine($"World: {s.Name}");
Console.WriteLine($"You are at: {s.Player?.GetPosition()}");
```

The same on **SE2** — the API root is `GameAppComponent`, reached through the
engine singleton, not `MyAPIGateway`:

```csharp
var engine = Singleton<VRageCore>.Instance.Engine;
var session = engine.Get<GameAppComponent>().ClientSession;
Console.WriteLine($"in a session: {session != null}");
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
> other plugins' Harmony hooks that execute there. The game API (SE1
> `MyAPIGateway`, SE2 session / scene access) asserts off the main thread.

Both clients also expose **`take_screenshot`** (captures the current frame as an
image; optional `ignore_sprites` to drop the HUD). Full parameters are in the
tool's `inputSchema`.

## Client vs. server

|                   | SE1 client (Pulsar)                | SE1 server (Magnetar)        | SE2 client (Pulsar Modern)      |
|-------------------|------------------------------------|------------------------------|---------------------------------|
| Default port      | `9876` (`9876–9885`)               | `9000` (`9000–9009`)         | `6789` (`6789–6798`)            |
| `render` lane     | ✅                                  | ❌                            | ✅                               |
| `parallel` lane   | ✅                                  | ✅                            | ✅                               |
| `take_screenshot` | ✅                                  | ❌                            | ✅                               |
| Multiplayer gate  | Admin/Owner required               | none — token is full access  | host / local-server only¹       |
| Settings GUI      | ✅ (MyGui)                          | ✅ (Quasar Plugin config)     | ✅ (Avalonia)                    |
| Config file       | `<UserDataPath>/Storage/SeMcp.cfg` | `<UserDataPath>/SeMcp.cfg`   | Pulsar `Data\SeMcp2\SeMcp2.cfg` |
| API root          | `MyAPIGateway` / `MySession`       | same                         | `GameAppComponent` (`Keen.*`)   |

¹ SE2 has no multiplayer yet; the gate pre-emptively blocks a pure client (one with
no local authoritative server), so in practice it only ever runs single-player today.

## Configuration

`Port` and `SecretKey` persist to the `.cfg` (auto-saved). On either client both are
editable in the settings dialog, with **Regenerate Token** and **Copy URL** buttons;
on the server they are editable through Quasar's **Plugin configuration** page.
**Changing the port requires a restart.**

## How it works

For anyone reading or extending the code:

- **`McpServer`** — one `HttpListener`, single (non-batched) JSON-RPC, auth +
  loopback-peer/CSRF/host checks, pre-rendered `tools/list`. Sessions namespace in-flight
  request ids but carry no server state.
- **`Executor`** — compilation runs on the thread pool; the compiled coroutine is
  then stepped one `MoveNext` per frame on its owning game thread. Each lane has
  its own executor and `ScriptGuard`, so finally-blocks and thread-affine state
  stay on the right thread.
- **`ParallelExecutor`** — the parallel lane. Scripts compile with `StackCheck`
  only and run inside `ControlledExecution.Run` (net48: a port of it), each with a
  `ScriptPump`: the queue of its continuations, which its thread runs until the
  script completes. A kill answers the request first, drops the continuations
  still to come, then aborts from a throwaway thread and interrupts managed waits
  — at once, then every 250 ms — which an abort alone can't wake on .NET 10.
- **`Compiler`** — drives Roslyn entirely through **reflection** (no compile-time
  binding, so it works against both the game's ancient Roslyn and the NuGet 5.3
  one), references what `ScriptReferences` collects (keeping only each file's
  metadata in memory), and enables **ignore-accessibility** so
  scripts can reach `internal` members: `MetadataImportOptions.Internal` +
  `BinderFlags.IgnoreAccessibility` at compile time, plus an injected
  `[assembly: IgnoresAccessChecksTo]` per referenced assembly at runtime (its
  attribute is self-declared — source wins over the copies Harmony and other
  plugins ship, so no ambiguity). It then rewrites the emitted IL with
  **Mono.Cecil** to inject the guard, and points the script's reference to each
  BCL async method builder at SeMcp's own.
- **`ScriptBuilders`** — those builders, member for member the same as the BCL's,
  so retargeting one type reference swaps the builder of every async method in a
  script without touching an instruction. `async ValueTask` methods run on the
  `Task` ones: their one call that names `ValueTask` is rewritten to make it from
  the `Task`, so the plugin never names a `ValueTask` of its own. On a parallel
  script's thread they wrap each awaiter, sending its continuation through the
  script's `ScriptPump`; anywhere else they pass the script's own call through.
  A faulted `Task` of theirs counts as observed.
- **`ScriptReferences`** — what scripts compile against: for each assembly name,
  the first file the runtime's binder would come to (loaded, then TPA on .NET 10,
  the loader's library folder, the game folder, the runtime folder on .NET
  Framework), read as metadata only. One that would change what a name already
  means in a script (a type's full name defined a second time, or a type with no
  namespace hiding the one a `using` brings in) goes in under an extern alias.
- **`ScriptGuard`** — a stack-depth `StackCheck` and a `Bail()` at the entry of
  every script method, `Bail()` on backward branches, and `catch`→filter rewrites
  (so a `catch` can't swallow the abort). Entry checks also cover script code the
  BCL or the game calls back into — an overridden `ToString`, a lambda handed to
  LINQ, a Harmony patch — so recursion that detours through foreign code is
  stopped, not a stack overflow. Each script bakes its own id into those checks;
  when a frame's 1 s budget runs out, the lane's `FrameWatchdog` raises the id of
  the script on the stack (`KillId`), and the checks answer only on the lane's own
  thread — so only that script is aborted, only there: never another script, never
  the same script's code on other threads, and never the Harmony patches or
  handlers an earlier script left behind.

## License

MIT — see [LICENSE](LICENSE).
