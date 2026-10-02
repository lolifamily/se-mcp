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
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin, ICommonPlugin
{
    private const string Name = "SeMcp";

    // Returned to the caller as the WorkItem error when IsDenied trips. Kept here
    // (not in Shared) because the wording is SE-business-specific (Admin/Owner
    // terminology, "in multiplayer" framing) — Shared.Executor stays string-neutral.
    // The tool description says nothing of the gate: single player never meets it,
    // a non-admin's first call gets this message instead, and no model can promote
    // itself — knowing it sooner changes no call.
    private const string DenialMessage =
        "Code execution needs Admin or Owner promote level in multiplayer.";

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
    // SeMcp 0.x kept its .cfg under UserDataPath\Storage\; preserved here so
    // existing users' stored tokens load unchanged.
    private const string ConfigFileName = $"{Name}.cfg";
    private const string ConfigSubDir = "Storage";

    private static SettingsScreen _settingsDialog;
    internal static bool RefreshSettings;
    private SettingsGenerator settingsGenerator;

    // Three execution lanes:
    //   Main     — ticked by Pump, from Patch_MainLane's Postfix on SE's main
    //              thread (game/API state).
    //   Render   — ticked from Patch_RenderFrame's Postfix on SE's render thread,
    //              for inspecting plugin Harmony hooks that run there.
    //   Parallel — each script on a thread of its own; Pump only pushes the
    //              deny gate onto it.
    // Main and Render each bind to their own ScriptGuard{Main,Render} static
    // class — the lambda closes over that class's KillId, its BeginStep marks
    // each step's thread and stack baseline, and IL injection picks the matching
    // tokens at compile time.
    // ReSharper disable once InconsistentNaming
    internal static Executor MainExecutor;
    internal static Executor RenderExecutor;
    // ReSharper disable once InconsistentNaming
    private static ParallelExecutor ParallelExecutor;

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
            ScriptGuardMain.KillingMethod,
            v => ScriptGuardMain.KillId = v,
            ScriptGuardMain.BeginStep,
            DenialMessage,
            frameTimeoutMs: 1000,
            defaultUsings: ScriptDefaults.Usings);

        RenderExecutor = new Executor(
            ScriptGuardRender.BailMethod,
            ScriptGuardRender.StackCheckMethod,
            ScriptGuardRender.KillingMethod,
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
        // Executor.Dispose only sets `disposed` and fulfills inflight promises —
        // it does NOT touch `active`. Coroutine cleanup must run on the thread
        // that ran the script (finally blocks observe Thread.CurrentThread and
        // hold thread-affine D3D11 state). So:
        //   - Main:   we are on the main thread now. Dispose() then Tick() drains
        //             active on this thread (the script's owning thread). No pump
        //             runs after plugins unload, so this is the last chance.
        //   - Render: setting disposed=true is enough. The next RenderFrame
        //             Postfix hook drains active on the render thread (the
        //             script's owning thread). harmony is NOT unpatched —
        //             the hook stays in place so the drain has a chance to run;
        //             subsequent disposed-path Ticks are cheap no-ops.
        //   - Parallel: Dispose answers its requests and aborts its scripts; their
        //             threads are background threads, so nothing waits on them.
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
        ParallelExecutor?.Dispose();

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

    // Called by Patch_MainLane.
    internal static void Pump()
    {
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
        // ParallelExecutor.Initialize publishes the references the same way, and
        // EnforceDenyGate pushes the gate refreshed above onto running scripts.
        // MyFileSystem.ExePath is Bin64, the game folder InitShared asks for.
        Compiler.InitShared(MyFileSystem.ExePath);
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
