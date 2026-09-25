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
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
#endif

namespace Client2Plugin;

// SE2 client plugin entry point. Unlike SE1 (IPlugin.Init(gameInstance) + per-frame
// IPlugin.Update), SE2's IPlugin is an EMPTY interface: the constructor is the load hook,
// teardown requires implementing IDisposable (Pulsar Modern's PluginInstance.Dispose
// reflectively checks `plugin as IDisposable`, PluginInstance.cs:153), and the per-frame
// pumps are Harmony patches (PatchMainLane on VRageCore.Update, PatchRenderFrame on
// Render12EngineComponent.RenderFrame) rather than a native Update callback.
//
// Structurally one-for-one with SE1's ClientPlugin.Plugin: same PersistentConfig wiring,
// same two Executors bound to Shared/Core's ScriptGuard{Main,Render}, same McpServer +
// ExecuteCodeTool, same AssemblyResolve, same deny-gate cache. Only the host mechanics
// differ (constructor vs Init, IDisposable vs Dispose, patched pumps vs Update, Plugin2Logger
// vs PluginLogger, GameAppComponent session vs MyAPIGateway).
public sealed class Plugin : IPlugin, IDisposable, ICommonPlugin
{
    public const string Name = "SeMcp2";
    internal static Plugin Instance;

    // Suffix added to the execute_code tool description (LLM-facing reminder). The actual
    // gate is Config.Denied, refreshed each frame by PatchMainLane via GameAccess.
    private const string MpAdminNote =
        "Multiplayer (once SE2 ships it): only host / local-server sessions can execute; pure clients are blocked.";

    // Returned as the WorkItem error when the deny gate trips. SE-business-specific wording,
    // kept here (not in Shared) — Shared.Executor stays string-neutral.
    private const string DenialMessage =
        "Multiplayer client-only session: code execution is disabled — no local authoritative server present.";

    // DataDir provided by Pulsar via reflection, injected BEFORE construction
    // (PluginInstance.cs DependencyInject, before Activator.CreateInstance).
    // GetConfigPath(Name, null) → Pulsar's Data\{Name}\ directory (created on use).
#pragma warning disable CS0649 // assigned by Pulsar via reflection
    // ReSharper disable once InconsistentNaming
    private static Func<string, string, string> GetConfigPath;
#pragma warning restore CS0649
    private string DataDir { get; } = GetConfigPath(Name, null);

    private static readonly IPluginLogger Logger = new Plugin2Logger(Name);
    public IPluginLogger Log => Logger;

    // ICommonPlugin.Config returns the live runtime config. PersistentConfig owns persistence
    // (500ms auto-save on PropertyChanged); its Data is also published to Config.Current so
    // SettingsGenerator (which dereferences Config.Current statically) sees the same instance.
    public IPluginConfig Config => config?.Data;
    private PersistentConfig<Config> config;
    private const string ConfigFileName = $"{Name}.cfg";

    // Two execution lanes (mirrors SE1). Main is pumped by PatchMainLane (VRageCore.Update
    // Postfix); Render by PatchRenderFrame (Render12EngineComponent.RenderFrame Postfix). Each
    // binds its own ScriptGuard{Main,Render} static class (Shared/Core) via pre-resolved handles.
    internal static Executor MainExecutor;
    internal static Executor RenderExecutor;

    private McpServer mcpServer;

    // Handle to the currently-open Avalonia settings screen. Held so RefreshSettingsDialog can
    // close+reopen it when the token changes; nulled when the user closes the dialog.
    private IDisposable settingsScreenHandle;

    // AssemblyResolve (AppDomain-global) so compiled REPL scripts can resolve this plugin's own
    // assembly. Registered once here, unregistered in Dispose (matches SE1). Compiler keeps its
    // own per-instance resolver for Pulsar's LoadFile assemblies.
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;
    private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args) =>
        new AssemblyName(args.Name).Name == PluginAssembly.GetName().Name ? PluginAssembly : null;

    public Plugin()
    {
        Instance = this;
        Log.Info("Loading");

        // No top-level try/catch: Pulsar Modern's PluginInstance.Instantiate already wraps
        // construction and turns any throw into ThrowError (logs + marks Error + Disposes),
        // PluginInstance.cs:55-69. Same shape as SE1's Init — only Dispose is guarded.

        // GetConfigPath's Data\{Name}\ dir may not exist yet; PersistentConfig.Load writes a
        // default file if missing and File.CreateText needs the directory.
        Directory.CreateDirectory(DataDir);
        config = PersistentConfig<Config>.Load(Log, Path.Combine(DataDir, ConfigFileName));
        Client2Plugin.Config.Current = config.Data;

        // First-launch seeding. Empty → set through the setter, whose PropertyChanged schedules
        // PersistentConfig's 500ms auto-save (no manual Save).
        if (string.IsNullOrEmpty(config.Data.SecretKey))
            config.Data.SecretKey = TokenGenerator.Generate();

        // SE2 client default port 6789 (retries 6789-6798). Disjoint from SE1 client 9876-9885
        // and SE1 server 9000-9009 so all three run side by side.
        if (string.IsNullOrWhiteSpace(config.Data.Port))
            config.Data.Port = "6789";

        Common.SetPlugin(this);

        // Two lanes, each bound to its own ScriptGuard (Shared/Core). Identical to SE1's wiring.
        MainExecutor = new Executor(
            ScriptGuardMain.BailMethod, ScriptGuardMain.StackCheckMethod, ScriptGuardMain.KillIdField,
            v => ScriptGuardMain.KillId = v, sp => ScriptGuardMain.StackBase = sp,
            DenialMessage, frameTimeoutMs: 1000, defaultUsings: ScriptDefaults.Usings);

        RenderExecutor = new Executor(
            ScriptGuardRender.BailMethod, ScriptGuardRender.StackCheckMethod, ScriptGuardRender.KillIdField,
            v => ScriptGuardRender.KillId = v, sp => ScriptGuardRender.StackBase = sp,
            DenialMessage, frameTimeoutMs: 1000, defaultUsings: ScriptDefaults.Usings);

        var tools = new ITool[]
        {
            new ExecuteCodeTool(MainExecutor, RenderExecutor, MpAdminNote, ScriptDefaults.SchemaText),
            new ScreenshotTool(MainExecutor)
        };

        mcpServer = new McpServer(tools, config.Data);
        mcpServer.Start();

        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;

        if (!PatchHelpers.HarmonyPatchAll(Log, new Harmony(Name)))
            return;

        Log.Debug("Successfully loaded");
    }

    public void Dispose()
    {
        // PluginInstance.Dispose runs on the main thread (verified: GameApp.Dispose →
        // PluginHost → PluginLoader → PluginInstance, all main thread). So for the main lane,
        // Dispose (sets disposed + fulfills inflight) then Tick drains `active` on the
        // script's owning thread — finally blocks / thread-affine state stay correct. Render
        // lane: Dispose is enough; its next RenderFrame Postfix drains on the render thread
        // (the hook is left patched so that drain can still run).
        RenderExecutor?.Dispose();
        MainExecutor?.Dispose();
        MainExecutor?.Tick();

        AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
        Compiler.ReleaseShared();

        // McpServer after the executors: Dispose above fulfilled all inflight promises,
        // queueing each response write onto the pool; stopping the listener last gives those
        // writes a head start.
        mcpServer?.Dispose();

        // PersistentConfig: unsubscribe + release timer + one synchronous final Save().
        config?.Dispose();

        MainExecutor = null;
        RenderExecutor = null;
        mcpServer = null;
        config = null;
    }

    // Invoked by Pulsar via reflection when the user clicks the plugin's config button.
    private void OpenConfigDialog()
    {
        // No try/catch on the Pulsar path: Modern's PluginInstance.OpenConfig already wraps
        // the reflected Invoke (Modern/Loader/PluginInstance.cs:138). The OTHER caller —
        // RefreshSettingsDialog, off an Avalonia UI event — has no such wrapper and guards
        // the call on its own side.
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
            // onClose: forget the handle. No save-on-close — config commits on
            // PropertyChanged via PersistentConfig's 500ms auto-save (indexed plan D3).
            () => settingsScreenHandle = null);

        settingsScreenHandle = sharedUi.CreateScreen<SettingsScreen>(viewModel, showCursor: true);
    }

    // Re-render the settings dialog after the token changes so the new value shows in the
    // Textbox. SE2 screens are immutable once built (no SE1-style per-frame RecreateControls),
    // so we close the current screen and reopen — the rebuilt screen reads the fresh token.
    // No-op if the dialog isn't open. Invoked on the UI thread (button click / textbox edit);
    // closing the screen that owns the triggering button mid-callback is the same pattern the
    // built-in close button uses.
    internal void RefreshSettingsDialog()
    {
        if (settingsScreenHandle == null)
            return;

        // Invoked off a UI event (Config.SecretKey setter / RegenerateToken button), NOT
        // through Pulsar — so nothing upstream catches. OpenConfigDialog no longer guards
        // itself, so guard here: a screen-rebuild failure must not escape into the Avalonia
        // event dispatcher.
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
