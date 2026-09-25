using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using Shared.Config;
using Shared.Logging;
using Shared.Mcp;
using Shared.Patches;
using Shared.Plugin;
using Shared.Se1;
using VRage.FileSystem;
using VRage.Plugins;

// Define assembly version when compiled by Magnetar
#if !DEV_BUILD
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin, ICommonPlugin
{
    private const string Name = "SeMcp";

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    public IPluginConfig Config => config?.Data;
    private PersistentConfig<Config> config;

    [UsedImplicitly]
    public Config PluginConfig => config?.Data;
    // DS writes its .cfg directly under UserDataPath (no Storage/ subdir; the
    // server has no Settings GUI and its file layout follows the template default).
    private const string ConfigFileName = $"{Name}.cfg";

    // Single execution lane on DS: there's no render thread, no Patch_RenderFrame
    // hook. McpServer's `target` enum is derived from lanes.Keys → tools/list
    // exposes only ["main"] on the server, and a request that asks for "render"
    // gets a clean -32602 rather than a silent fallback.
    private static Executor _mainExecutor;

    private McpServer mcpServer;

    // AssemblyResolve: lets REPL-compiled scripts that reference the SeMcp.dll
    // type system (e.g. via `using Shared.Mcp;` or class_body that captures a
    // local of one of our types) resolve back to the loaded plugin assembly.
    // Compiler keeps its OWN per-instance resolver for Magnetar's LoadFile
    // assemblies — that one is registered/unregistered inside Compiler itself.
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args)
    {
        return new AssemblyName(args.Name).Name == PluginAssembly.GetName().Name
            ? PluginAssembly
            : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Log.Info("Loading");

        var configPath = Path.Combine(MyFileSystem.UserDataPath, ConfigFileName);
        config = PersistentConfig<Config>.Load(Log, configPath);
        ServerPlugin.Config.Instance = config.Data;

        // Empty SecretKey on first launch → mint one through the setter; the
        // base setter auto-generates via TokenGenerator and fires PropertyChanged,
        // which PersistentConfig flushes to disk on its next 500ms tick.
        if (string.IsNullOrEmpty(config.Data.SecretKey))
            config.Data.SecretKey = TokenGenerator.Generate();

        if (string.IsNullOrWhiteSpace(config.Data.Port))
            config.Data.Port = "9000";

        Common.SetPlugin(this);

        // Best-effort on DS. The only patch here is Patch_ConfigSchema, which
        // just relabels one caption in the config-screen schema. The executor
        // and McpServer started below are driven by the native IPlugin.Update
        // pump, not by any patch — so if Magnetar's ConfigSchema shape shifts
        // and PatchAll throws, the screen falls back to the default caption and
        // core code-execution keeps working. A cosmetic patch must not take the
        // plugin down with it: log and carry on.
        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            Log.Warning("Config-schema patch failed; using default caption. Core MCP/code-execution unaffected.");

        // Single lane on DS. denyPolicy returns false unconditionally — the DS
        // already gates who can join the server; once a caller has the SeMcp
        // bearer token they have full RCE, so a "must be Admin" check on top
        // would be security theater. denialMessage is unread in practice (the
        // for-loop reject path is never taken) but kept non-null as a guardrail
        // against a future code path that surfaces it.
        _mainExecutor = new Executor(
            ScriptGuardMain.BailMethod,
            ScriptGuardMain.StackCheckMethod,
            ScriptGuardMain.KillIdField,
            v => ScriptGuardMain.KillId = v,
            sp => ScriptGuardMain.StackBase = sp,
            denialMessage: "denied",
            frameTimeoutMs: 1000,
            defaultUsings: ScriptDefaults.Usings);

        // mpAdminNote omitted: server has no MP admin gate, the schema
        // description stays free of the "Multiplayer requires Admin" line.
        var tools = new ITool[] { new ExecuteCodeTool(_mainExecutor) };

        mcpServer = new McpServer(tools, config.Data);
        mcpServer.Start();

        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        // Single lane: Dispose() sets `disposed` and fulfills inflight
        // promises; Tick() then drains `active` on this thread (main).
        // After Dispose returns SE stops calling Update — this is the
        // last chance to run script finally blocks on the right thread.
        _mainExecutor?.Dispose();
        _mainExecutor?.Tick();

        AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
        Compiler.ReleaseShared();

        mcpServer?.Dispose();

        // PersistentConfig owns a PropertyChanged subscription and a save
        // timer. Dispose unsubscribes, releases the timer, and does one
        // synchronous final Save() — covers any change made inside the
        // last 500ms save window. Independent of listener / executor
        // teardown, so ordering doesn't matter; placed last.
        config?.Dispose();

        ServerPlugin.Config.Instance = null;
        _mainExecutor = null;
        mcpServer = null;
        config = null;
    }

    public void Update()
    {
        // InitShared / Initialize are self-guarded (return after the first
        // call). Order matters: shared compiler references must populate
        // before MainExecutor exposes Initialized=true to the McpServer gate
        // (the volatile write is also what publishes them across threads).
        Compiler.InitShared();
        _mainExecutor?.Initialize();
        _mainExecutor?.Tick();
    }
}
