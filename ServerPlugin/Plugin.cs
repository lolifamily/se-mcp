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
using VRage.Game;
using VRage.Plugins;

// Define assembly version when compiled by Magnetar
#if !DEV_BUILD
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
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

    // Two execution lanes on DS, main and parallel: there's no render thread, no
    // Patch_RenderFrame hook. ExecuteCodeTool's `target` enum comes from the lanes
    // it is given → tools/list exposes ["main","parallel"] on the server, and a
    // request that asks for "render" gets a clean -32602 rather than a silent
    // fallback.
    private static Executor _mainExecutor;
    private static ParallelExecutor _parallelExecutor;

    // No deny gate on DS (see Init), so no request ever reads this; kept non-null
    // as a guardrail against a future code path that surfaces it.
    private const string DenialMessage = "denied";

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

        // No patches, no main lane pump: nothing to serve. PatchHelpers logs why.
        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            return;

        // No deny gate on DS: denyPolicy returns false unconditionally — the DS
        // already gates who can join the server; once a caller has the SeMcp
        // bearer token they have full RCE, so a "must be Admin" check on top
        // would be security theater.
        _mainExecutor = new Executor(
            ScriptGuardMain.BailMethod,
            ScriptGuardMain.StackCheckMethod,
            ScriptGuardMain.KillingMethod,
            v => ScriptGuardMain.KillId = v,
            ScriptGuardMain.BeginStep,
            DenialMessage,
            frameTimeoutMs: 1000,
            defaultUsings: ScriptDefaults.Usings);

        _parallelExecutor = new ParallelExecutor(DenialMessage, ScriptDefaults.Usings);

        // No render lane on DS (renderExec null): the schema offers main and parallel.
        var tools = new ITool[]
        {
            new ExecuteCodeTool(_mainExecutor, _parallelExecutor, null, ScriptDefaults.Game, ScriptDefaults.Imports)
        };

        mcpServer = new McpServer(tools, config.Data,
            $"Space Engineers {MyFinalBuildConstants.APP_VERSION_STRING_DOTS}", dedicated: true);
        mcpServer.Start();

        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        // Main: Dispose() sets `disposed` and fulfills inflight promises;
        // Tick() then drains `active` on this thread (main). No pump runs after
        // plugins unload — this is the last chance to run script finally blocks
        // on the right thread. Parallel: Dispose answers its requests and aborts
        // its scripts; their threads are background threads.
        _mainExecutor?.Dispose();
        _mainExecutor?.Tick();
        _parallelExecutor?.Dispose();

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
        _parallelExecutor = null;
        mcpServer = null;
        config = null;
    }

    // Unused: Patch_MainLane pumps the main lane.
    public void Update()
    {
    }

    // Called by Patch_MainLane.
    internal static void Pump()
    {
        // InitShared / Initialize are self-guarded (return after the first
        // call). Order matters: shared compiler references must populate
        // before MainExecutor exposes Initialized=true to the McpServer gate
        // (the volatile write is also what publishes them across threads).
        // ParallelExecutor publishes them the same way. MyFileSystem.ExePath is
        // DedicatedServer64, the game folder InitShared asks for.
        Compiler.InitShared(MyFileSystem.ExePath);
        _mainExecutor?.Initialize();
        _parallelExecutor?.Initialize();
        _mainExecutor?.Tick();
        _parallelExecutor?.EnforceDenyGate();
    }
}
