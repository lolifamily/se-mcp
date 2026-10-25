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
[assembly: AssemblyVersion("2.3.1.0")]
[assembly: AssemblyFileVersion("2.3.1.0")]
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
    // Under UserDataPath itself, as the template has it; the client's Storage\ is for SeMcp 0.x.
    private const string ConfigFileName = $"{Name}.cfg";

    // Main and parallel: a dedicated server has no render thread.
    private static Executor _mainExecutor;
    private static ParallelExecutor _parallelExecutor;

    // Never shown: the server's gate never closes (Config.Denied), as the server decides who joins
    // and the token runs any code anyway. Non-null in case a path ever shows it.
    private const string DenialMessage = "denied";

    private McpServer mcpServer;

    // Hands this plugin's assembly to anyone asking for it by name, scripts that use its types
    // included. Compiler's resolver answers only scripts, with assemblies loaded by LoadFile.
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

        // First launch: a token and the default port, saved like any other change.
        if (string.IsNullOrEmpty(config.Data.SecretKey))
            config.Data.SecretKey = TokenGenerator.Generate();

        if (string.IsNullOrWhiteSpace(config.Data.Port))
            config.Data.Port = "9000";

        Common.SetPlugin(this);

        // No patches, no main lane pump: nothing to serve. PatchHelpers logs why.
        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            return;

        _mainExecutor = new Executor(
            ScriptGuardMain.KillIdField,
            ScriptGuardMain.LaneThreadField,
            ScriptGuardMain.StackCheckMethod,
            v => ScriptGuardMain.KillId = v,
            ScriptGuardMain.BeginStep,
            DenialMessage,
            frameTimeoutMs: 1000,
            defaultUsings: ScriptDefaults.Usings);

        _parallelExecutor = new ParallelExecutor(DenialMessage, ScriptDefaults.Usings);

        // No render lane: the schema offers main and parallel, and refuses "render".
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
        // The main lane's last Tick, for its scripts' finally blocks: no pump comes after this.
        // Parallel scripts are aborted.
        _mainExecutor?.Dispose();
        _mainExecutor?.Tick();
        _parallelExecutor?.Dispose();

        AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
        Compiler.ReleaseShared();

        mcpServer?.Dispose();

        // One last save, for a change still waiting out the save delay. Order doesn't matter here.
        config?.Dispose();

        ServerPlugin.Config.Instance = null;
        _mainExecutor = null;
        _parallelExecutor = null;
        mcpServer = null;
        config = null;
    }

    // Unused: PatchMainLane pumps the main lane.
    public void Update()
    {
    }

    // Called by PatchMainLane.
    internal static void Pump()
    {
        // The lanes open only once the script references are in (InitShared).
        // MyFileSystem.ExePath is DedicatedServer64, the game folder InitShared asks for.
        if (!Compiler.InitShared(MyFileSystem.ExePath)) return;
        _mainExecutor?.Initialize();
        _parallelExecutor?.Initialize();
        _mainExecutor?.Tick();
        _parallelExecutor?.EnforceDenyGate();
    }
}
