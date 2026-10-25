using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using JetBrains.Annotations;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using Shared.Config;
using Shared.Logging;
using Shared.Mcp;
using Shared.Patches;
using Shared.Plugin;
using Shared.Se1;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !DEV_BUILD
[assembly: AssemblyVersion("2.3.1.0")]
[assembly: AssemblyFileVersion("2.3.1.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin, ICommonPlugin
{
    private const string Name = "SeMcp";

    // A request's error while the deny gate is closed (IsSeAdminDenied). Not in the tool description:
    // single player never meets it, a non-admin's first call says it, and knowing sooner changes no
    // call, as no model can promote itself.
    private const string DenialMessage =
        "Code execution needs Admin or Owner promote level in multiplayer.";

    private static bool _failed;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    // The live config, which PersistentConfig saves on every change. Init also makes it
    // Config.Current, the instance the settings dialog edits. Null before Init.
    public IPluginConfig Config => config?.Data;
    private PersistentConfig<Config> config;
    // Where SeMcp 0.x kept its .cfg (UserDataPath\Storage\), so existing tokens still load.
    private const string ConfigFileName = $"{Name}.cfg";
    private const string ConfigSubDir = "Storage";

    private static SettingsScreen _settingsDialog;
    internal static bool RefreshSettings;
    private SettingsGenerator settingsGenerator;

    // The lanes (ExecuteCodeTool): main and render are stepped from their patches (PatchMainLane
    // through Pump, PatchRenderFrame), each with its own ScriptGuard class; parallel runs on threads
    // of its own, and Pump only pushes the deny gate onto it.
    // ReSharper disable once InconsistentNaming
    internal static Executor MainExecutor;
    internal static Executor RenderExecutor;
    // ReSharper disable once InconsistentNaming
    private static ParallelExecutor ParallelExecutor;

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

    // The deny gate: closed in multiplayer below Admin. Main thread only, as MyAPIGateway.Session
    // assert-throws elsewhere; Pump stores the answer in Config.Denied for every other thread.
    private static bool IsSeAdminDenied()
    {
        var session = MyAPIGateway.Session;
        if (session == null || session.OnlineMode == MyOnlineModeEnum.OFFLINE)
            return false;
        return session.PromoteLevel < MyPromoteLevel.Admin;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        Log.Info("Loading");

        // Load writes a default file when there is none, and File.CreateText needs the directory.
        var configDir = Path.Combine(MyFileSystem.UserDataPath, ConfigSubDir);
        Directory.CreateDirectory(configDir);
        config = PersistentConfig<Config>.Load(Log, Path.Combine(configDir, ConfigFileName));
        ClientPlugin.Config.Current = config.Data;

        // First launch: a token and the default port, saved like any other change.
        if (string.IsNullOrEmpty(config.Data.SecretKey))
            config.Data.SecretKey = TokenGenerator.Generate();

        if (string.IsNullOrWhiteSpace(config.Data.Port))
            config.Data.Port = "9876";

        Common.SetPlugin(this);

        MainExecutor = new Executor(
            ScriptGuardMain.KillIdField,
            ScriptGuardMain.LaneThreadField,
            ScriptGuardMain.StackCheckMethod,
            v => ScriptGuardMain.KillId = v,
            ScriptGuardMain.BeginStep,
            DenialMessage,
            frameTimeoutMs: 1000,
            defaultUsings: ScriptDefaults.Usings);

        RenderExecutor = new Executor(
            ScriptGuardRender.KillIdField,
            ScriptGuardRender.LaneThreadField,
            ScriptGuardRender.StackCheckMethod,
            v => ScriptGuardRender.KillId = v,
            ScriptGuardRender.BeginStep,
            DenialMessage,
            frameTimeoutMs: 1000,
            defaultUsings: ScriptDefaults.Usings);

        ParallelExecutor = new ParallelExecutor(DenialMessage, ScriptDefaults.Usings);

        var tools = new ITool[]
        {
            new ExecuteCodeTool(MainExecutor, ParallelExecutor, RenderExecutor, ScriptDefaults.Game, ScriptDefaults.Imports),
            new ScreenshotTool(MainExecutor)
        };

        mcpServer = new McpServer(tools, config.Data,
            $"Space Engineers {MyFinalBuildConstants.APP_VERSION_STRING_DOTS}", dedicated: false);
        mcpServer.Start();

        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;

        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
        {
            _failed = true;
            return;
        }

        settingsGenerator = new SettingsGenerator();
        _settingsDialog = settingsGenerator.Dialog;

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        // A frame lane's scripts still owe their finally blocks, run on the lane's own thread at its
        // next Tick (Executor.Dispose). The main lane gets that Tick here, as no pump comes after
        // this; the render lane gets it from its next frame, which is why its patch stays in place.
        // Parallel scripts are aborted, on background threads nothing waits for.
        RenderExecutor?.Dispose();
        MainExecutor?.Dispose();
        MainExecutor?.Tick();
        ParallelExecutor?.Dispose();

        AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
        Compiler.ReleaseShared();

        // A screenshot in flight is answered too.
        ScreenshotService.Drain();

        // Last: the answers above are being written on the thread pool, and stopping the listener
        // cuts off what isn't done. Going last gives those writes a head start; one that loses the
        // race is logged and dropped.
        mcpServer?.Dispose();

        // One last save, for a change still waiting out the save delay. Order doesn't matter here.
        config?.Dispose();

        MainExecutor = null;
        RenderExecutor = null;
        ParallelExecutor = null;
        mcpServer = null;
        config = null;
    }

    public void Update()
    {
        if (_failed)
            return;

        if (!RefreshSettings) return;
        RefreshSettings = false;
        _settingsDialog?.RecreateControls(false);
    }

    // Called by PatchMainLane.
    internal static void Pump()
    {
        // Refreshed once a frame, here: the main thread is the one IsSeAdminDenied may run on.
        ClientPlugin.Config.Current.Denied = IsSeAdminDenied();

        // The lanes open only once the script references are in (InitShared); render opens on its
        // own thread (PatchRenderFrame). MyFileSystem.ExePath is Bin64, the game folder InitShared
        // asks for.
        if (!Compiler.InitShared(MyFileSystem.ExePath)) return;
        MainExecutor?.Initialize();
        ParallelExecutor?.Initialize();
        MainExecutor?.Tick();
        ParallelExecutor?.EnforceDenyGate();
        ScreenshotService.Tick();
    }

    [UsedImplicitly]
    public void OpenConfigDialog()
    {
        settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(settingsGenerator.Dialog);
    }
}
