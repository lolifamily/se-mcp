using System;
using System.Reflection;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using JetBrains.Annotations;
using Sandbox.Graphics.GUI;
using VRage.Plugins;

#if !DEV_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ClientPlugin;

[UsedImplicitly]
public class Plugin : IPlugin
{
    public const string Name = "SeMcp";
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
    private Harmony harmony;

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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        if (string.IsNullOrEmpty(Config.Current.SecretKey))
        {
            Config.Current.SecretKey = Config.GenerateToken();
            ConfigStorage.Save(Config.Current);
        }

        MainExecutor = new Executor(
            ScriptGuardMain.BailMethod,
            ScriptGuardMain.StackCheckMethod,
            ScriptGuardMain.DeadField,
            v => ScriptGuardMain.Dead = v,
            sp => ScriptGuardMain.StackBase = sp,
            frameTimeoutMs: 1000);

        RenderExecutor = new Executor(
            ScriptGuardRender.BailMethod,
            ScriptGuardRender.StackCheckMethod,
            ScriptGuardRender.DeadField,
            v => ScriptGuardRender.Dead = v,
            sp => ScriptGuardRender.StackBase = sp,
            frameTimeoutMs: 1000);

        mcpServer = new McpServer(MainExecutor, RenderExecutor, Config.Current.Port);
        mcpServer.Start();

        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;

        harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        settingsGenerator = new SettingsGenerator();
        _settingsDialog = settingsGenerator.Dialog;
    }

    public void Dispose()
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

        MainExecutor = null;
        RenderExecutor = null;
        mcpServer = null;
        harmony = null;
    }

    public void Update()
    {
        if (RefreshSettings)
        {
            RefreshSettings = false;
            _settingsDialog?.RecreateControls(false);
        }
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
