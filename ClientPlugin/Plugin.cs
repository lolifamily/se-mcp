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
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;

// Define assembly version when compiled by Pulsar
#if !DEV_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
[UsedImplicitly]
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "SeMcp";

    // Suffix added to the execute_code tool description (LLM-facing reminder).
    // The actual gate is the IsDenied lambda below — the description just primes
    // the LLM to expect it.
    private const string MpAdminNote =
        "Multiplayer requires Admin or Owner promote level.";

    // Returned to the caller as the WorkItem error when IsDenied trips. Kept here
    // (not in Shared) because the wording is SE-business-specific (Admin/Owner
    // terminology, "in multiplayer" framing) — Shared.Executor stays string-neutral.
    private const string DenialMessage =
        "Multiplayer non-admin: code execution is disabled. You must be Admin or Owner to use SeMcp in multiplayer.";

    private static bool _failed;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    // ICommonPlugin.Config returns the live runtime config. The PersistentConfig
    // wrapper owns persistence (500ms auto-save on PropertyChanged); its Data is
    // ALSO published to ClientPlugin.Config.Current so SE's SettingsGenerator
    // (which dereferences Config.Current statically) sees the same live instance.
    // config?.Data because the wrapper is null before Init runs.
    public IPluginConfig Config => config?.Data;
    private PersistentConfig<Config> config;
    // SeMcp 0.x kept its .cfg under UserDataPath\Storage\ (the legacy
    // ConfigStorage convention); preserved here so existing users' stored tokens
    // load unchanged.
    private const string ConfigFileName = $"{Name}.cfg";
    private const string ConfigSubDir = "Storage";

    private static SettingsScreen _settingsDialog;
    internal static bool RefreshSettings;
    private SettingsGenerator settingsGenerator;

    // Two execution lanes:
    //   Main   — ticked from IPlugin.Update on SE's main thread (game/API state).
    //   Render — ticked from Patch_RenderFrame's Postfix on SE's render thread,
    //            for inspecting plugin Harmony hooks that run there.
    // Each binds to its own ScriptGuard{Main,Render} static class — the lambdas
    // close over those classes' Dead / StackBase fields, and IL injection picks
    // the matching tokens at compile time.
    // ReSharper disable once InconsistentNaming
    internal static Executor MainExecutor;
    internal static Executor RenderExecutor;

    private McpServer mcpServer;

    // AssemblyResolve is an AppDomain-global multicast event. Registering the same
    // static handler twice (once per Executor) would re-invoke it on every resolve.
    // Register here exactly once across both executors, paired with the matching
    // unregister in Dispose. Compiler keeps its own per-instance resolver for
    // Pulsar's LoadFile assemblies — that one has different state per Executor and
    // is correctly registered/unregistered inside Compiler itself.
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args)
    {
        return new AssemblyName(args.Name).Name == PluginAssembly.GetName().Name
            ? PluginAssembly
            : null;
    }

    // The deny gate, injected into Executor. Executor caches the result of this
    // call (volatile bool) at the top of each Tick on the owner thread (main or
    // render) and Enqueue reads the cache — so MyAPIGateway.Session, which
    // assert-throws off the main thread, is never accessed from the ThreadPool.
    // Same lambda is given to both executors; render thread reads of Session are
    // a static-field load and benign in practice.
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

        // PersistentConfig.Load auto-writes a default file if missing; its directory
        // must exist or File.CreateText throws. SEMCP 0.x's Storage/ subdir survives
        // here for user-file back-compat (see ConfigFileName comment above).
        var configDir = Path.Combine(MyFileSystem.UserDataPath, ConfigSubDir);
        Directory.CreateDirectory(configDir);
        config = PersistentConfig<Config>.Load(Log, Path.Combine(configDir, ConfigFileName));
        // Publish the live instance to the static SE-GUI access point. Both
        // SettingsGenerator (Config.Current.Title, GetValue/SetValue) and the
        // McpServer constructor below now read/write the same Config — PropertyChanged
        // edits route through PersistentConfig's auto-save Timer.
        ClientPlugin.Config.Current = config.Data;

        // First-launch SecretKey seeding. Empty in the .cfg → mint one through
        // the setter; PropertyChanged then schedules PersistentConfig's 500ms
        // auto-save. No manual Save needed.
        if (string.IsNullOrEmpty(config.Data.SecretKey))
            config.Data.SecretKey = TokenGenerator.Generate();

        if (string.IsNullOrWhiteSpace(config.Data.Port))
            config.Data.Port = "9876";

        Common.SetPlugin(this);

        MainExecutor = new Executor(
            ScriptGuardMain.BailMethod,
            ScriptGuardMain.StackCheckMethod,
            ScriptGuardMain.DeadField,
            v => ScriptGuardMain.Dead = v,
            sp => ScriptGuardMain.StackBase = sp,
            DenialMessage,
            frameTimeoutMs: 1000);

        RenderExecutor = new Executor(
            ScriptGuardRender.BailMethod,
            ScriptGuardRender.StackCheckMethod,
            ScriptGuardRender.DeadField,
            v => ScriptGuardRender.Dead = v,
            sp => ScriptGuardRender.StackBase = sp,
            DenialMessage,
            frameTimeoutMs: 1000);

        var tools = new ITool[]
        {
            new ExecuteCodeTool(MainExecutor, RenderExecutor, MpAdminNote),
            new ScreenshotTool(MainExecutor)
        };

        mcpServer = new McpServer(tools, config.Data);
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
        try
        {
            // Executor.Dispose only sets `disposed` and fulfills inflight promises —
            // it does NOT touch `active`. Coroutine cleanup must run on the thread
            // that ran the script (finally blocks observe Thread.CurrentThread and
            // hold thread-affine D3D11 state). So:
            //   - Main:   we are on the main thread now. Dispose() then Tick() drains
            //             active on this thread (the script's owning thread). After
            //             Plugin.Dispose returns SE stops calling Update, so this is
            //             the last chance.
            //   - Render: setting disposed=true is enough. The next RenderFrame
            //             Postfix hook drains active on the render thread (the
            //             script's owning thread). harmony is NOT unpatched —
            //             the hook stays in place so the drain has a chance to run;
            //             subsequent disposed-path Ticks are cheap no-ops.
            // McpServer is disposed last because HttpListener.Stop also cuts inflight
            // response streams. The Dispose() calls above fulfilled all inflight
            // promises, queueing each HandleToolsCall continuation (the response
            // write) onto the thread pool — WorkItem.Done uses
            // RunContinuationsAsynchronously. Stopping the listener last gives those
            // writes a head start; any that lose the race are logged and dropped by
            // HandleToolsCall's catch.
            RenderExecutor?.Dispose();
            MainExecutor?.Dispose();
            MainExecutor?.Tick();

            AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
            Compiler.ReleaseShared();

            // Same ordering contract as the Executors above: fulfill the pending
            // screenshot promise first so its HandleToolsCall continuation gets a
            // chance to write the response before the listener is stopped.
            ScreenshotService.Drain();

            mcpServer?.Dispose();

            // PersistentConfig owns a PropertyChanged subscription and a save
            // timer. Dispose unsubscribes, releases the timer, and does one
            // synchronous final Save() — covers any change made inside the
            // last 500ms save window. Independent of listener / executor
            // teardown, so ordering doesn't matter; placed last.
            config?.Dispose();

            MainExecutor = null;
            RenderExecutor = null;
            mcpServer = null;
            config = null;
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
        }
    }

    public void Update()
    {
        if (_failed)
            return;

        if (RefreshSettings)
        {
            RefreshSettings = false;
            _settingsDialog?.RecreateControls(false);
        }

        // Refresh the deny gate on the main thread once per frame. Other threads
        // (Enqueue from the ThreadPool, RenderExecutor.Tick on render) read it
        // through Common.Config.Denied — bool atomic, at most one frame stale.
        ClientPlugin.Config.Current.Denied = IsSeAdminDenied();

        // InitShared / Initialize are self-guarded (return after the first call);
        // cost on subsequent frames is a static-bool read each. Order matters:
        // shared compiler references must populate before MainExecutor exposes
        // Initialized=true to the McpServer gate — that volatile write is also
        // what publishes them across threads (see Compiler._sharedInit notes).
        // RenderExecutor.Initialize deliberately does NOT happen here: it lives
        // in PatchRenderFrame on the render lane's own pump, so the flag means
        // "this lane's pump is alive". In StartSync mode (RenderFrame never
        // ticks the render lane) render-targeted requests then keep getting
        // -32002 instead of compiling into a queue nothing ever drains.
        Compiler.InitShared();
        MainExecutor?.Initialize();
        MainExecutor?.Tick();
        ScreenshotService.Tick();
    }

    [UsedImplicitly]
    public void OpenConfigDialog()
    {
        settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(settingsGenerator.Dialog);
    }
}
