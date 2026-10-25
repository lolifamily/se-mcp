using System;
using System.IO;
using System.Reflection;
using Client2Plugin.Settings;
using Client2Plugin.Tools;
using HarmonyLib;
using Keen.VRage.Core.Plugins;
using Shared.Config;
using Shared.Logging;
using Shared.Mcp;
using Shared.Patches;
using Shared.Plugin;
using Shared.Se2;

// Define assembly version when compiled by Pulsar
#if !DEV_BUILD
[assembly: AssemblyVersion("2.3.1.0")]
[assembly: AssemblyFileVersion("2.3.1.0")]
#endif

namespace Client2Plugin;

// SE2's IPlugin is an empty interface: the constructor loads the plugin, and Pulsar disposes one
// that is IDisposable (PluginInstance.Dispose). Otherwise this is SE1's ClientPlugin.Plugin, one for
// one; only the host's mechanics differ (Plugin2Logger, SessionGate for the deny gate, an Avalonia
// settings screen).
public sealed class Plugin : IPlugin, IDisposable, ICommonPlugin
{
    public const string Name = "SeMcp2";
    internal static Plugin Instance;

    // A request's error while the deny gate is closed: a client with no local server, which only
    // multiplayer makes, once SE2 ships it (SessionGate). Not in the tool description, for SE1's
    // reasons.
    private const string DenialMessage =
        "Code execution needs a local server; this is a client-only multiplayer session.";

    // Set by Pulsar before the constructor runs (PluginInstance.DependencyInject).
    // GetConfigPath(Name, null) is Pulsar's Data\{Name}\.
#pragma warning disable CS0649 // assigned by Pulsar via reflection
    // ReSharper disable once InconsistentNaming
    private static Func<string, string, string> GetConfigPath;
#pragma warning restore CS0649
    private string DataDir { get; } = GetConfigPath(Name, null);

    private static readonly IPluginLogger Logger = new Plugin2Logger(Name);
    public IPluginLogger Log => Logger;

    // The live config, which PersistentConfig saves on every change. The constructor also makes it
    // Config.Current, the instance the settings screen edits.
    public IPluginConfig Config => config?.Data;
    private PersistentConfig<Config> config;
    private const string ConfigFileName = $"{Name}.cfg";

    // As on SE1: main and render are stepped from PatchMainLane and PatchRenderFrame, each with its
    // own ScriptGuard class; parallel runs on threads of its own, and PatchMainLane only pushes the
    // deny gate onto it.
    internal static Executor MainExecutor;
    internal static Executor RenderExecutor;
    internal static ParallelExecutor ParallelExecutor;

    private McpServer mcpServer;

    // The open settings screen, for RefreshSettingsDialog to reopen; null when none is open.
    private IDisposable settingsScreenHandle;

    // Hands this plugin's assembly to anyone asking for it by name, scripts that use its types
    // included. Compiler's resolver answers only scripts, with assemblies loaded by LoadFile.
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;
    private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args) =>
        new AssemblyName(args.Name).Name == PluginAssembly.GetName().Name ? PluginAssembly : null;

    public Plugin()
    {
        Instance = this;
        Log.Info("Loading");

        // No try/catch: Pulsar catches a throwing constructor, logs it, marks the plugin failed and
        // disposes it (PluginInstance.Instantiate).

        // Data\{Name}\ may not exist yet: Load writes a default file when there is none, and
        // File.CreateText needs the directory.
        Directory.CreateDirectory(DataDir);
        config = PersistentConfig<Config>.Load(Log, Path.Combine(DataDir, ConfigFileName));
        Client2Plugin.Config.Current = config.Data;

        // First launch: a token, saved like any other change.
        if (string.IsNullOrEmpty(config.Data.SecretKey))
            config.Data.SecretKey = TokenGenerator.Generate();

        // SE2 client default port 6789 (retries 6789-6798). Disjoint from SE1 client 9876-9885
        // and SE1 server 9000-9009 so all three run side by side.
        if (string.IsNullOrWhiteSpace(config.Data.Port))
            config.Data.Port = "6789";

        Common.SetPlugin(this);

        MainExecutor = new Executor(
            ScriptGuardMain.KillIdField, ScriptGuardMain.LaneThreadField, ScriptGuardMain.StackCheckMethod,
            v => ScriptGuardMain.KillId = v, ScriptGuardMain.BeginStep,
            DenialMessage, frameTimeoutMs: 1000, defaultUsings: ScriptDefaults.Usings);

        RenderExecutor = new Executor(
            ScriptGuardRender.KillIdField, ScriptGuardRender.LaneThreadField, ScriptGuardRender.StackCheckMethod,
            v => ScriptGuardRender.KillId = v, ScriptGuardRender.BeginStep,
            DenialMessage, frameTimeoutMs: 1000, defaultUsings: ScriptDefaults.Usings);

        ParallelExecutor = new ParallelExecutor(DenialMessage, ScriptDefaults.Usings);

        var tools = new ITool[]
        {
            new ExecuteCodeTool(MainExecutor, ParallelExecutor, RenderExecutor, ScriptDefaults.Game, ScriptDefaults.Imports),
            new ScreenshotTool(MainExecutor)
        };

        mcpServer = new McpServer(tools, config.Data,
            $"Space Engineers 2 {typeof(Keen.Game2.GameAppComponent).Assembly.GetName().Version}", dedicated: false);
        mcpServer.Start();

        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;

        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            return;

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        // Pulsar disposes on the main thread (checked: GameApp.Dispose → PluginHost → PluginLoader →
        // PluginInstance), so the main lane gets its last Tick here, as on SE1; the render lane gets it
        // from its next frame, its patch left in place for that. Parallel scripts are aborted.
        RenderExecutor?.Dispose();
        MainExecutor?.Dispose();
        MainExecutor?.Tick();
        ParallelExecutor?.Dispose();

        AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
        Compiler.ReleaseShared();

        // Last: the answers above are being written on the thread pool, and stopping the listener
        // cuts off what isn't done. Going last gives those writes a head start.
        mcpServer?.Dispose();

        // One last save, for a change still waiting out the save delay. Order doesn't matter here.
        config?.Dispose();

        MainExecutor = null;
        RenderExecutor = null;
        ParallelExecutor = null;
        mcpServer = null;
        config = null;
    }

    // Invoked by Pulsar via reflection when the user clicks the plugin's config button.
    private void OpenConfigDialog()
    {
        // No try/catch: Pulsar wraps its call already (PluginInstance.OpenConfig), and
        // RefreshSettingsDialog guards its own.
        var sharedUi = GameAccess.GetSharedUI();
        if (sharedUi == null)
        {
            Log.Warning("SharedUIComponent not available");
            return;
        }

        var generator = new SettingsGenerator();
        var viewModel = new SettingsScreenViewModel(
            generator.Title,
            generator.PopulateContent,
            // onClose: forget the handle. Nothing to save: every change is saved already.
            () => settingsScreenHandle = null);

        settingsScreenHandle = sharedUi.CreateScreen<SettingsScreen>(viewModel, showCursor: true);
    }

    // Shows a new token in the open settings screen, if any. An SE2 screen can't change once built
    // (SE1 recreates its controls every frame), so it is closed and reopened. UI thread; closing the
    // screen from its own button's callback is what the built-in close button does too.
    internal void RefreshSettingsDialog()
    {
        if (settingsScreenHandle == null)
            return;

        // Called from a UI event, which nothing upstream guards: a failure here must not reach
        // Avalonia's event dispatcher.
        try
        {
            settingsScreenHandle.Dispose();
            settingsScreenHandle = null;
            OpenConfigDialog();
        }
        catch (Exception e)
        {
            Log.Error(e, "RefreshSettingsDialog failed");
        }
    }
}
