using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using JetBrains.Annotations;
using Sandbox.Graphics.GUI;
using VRage.Plugins;

#if !DEV_BUILD
using System.Reflection;
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
    private Executor executor;
    private McpServer mcpServer;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        if (string.IsNullOrEmpty(Config.Current.SecretKey))
        {
            Config.Current.SecretKey = Config.GenerateToken();
            ConfigStorage.Save(Config.Current);
        }

        executor = new Executor();
        mcpServer = new McpServer(executor, Config.Current.Port);
        mcpServer.Start();

        settingsGenerator = new SettingsGenerator();
        _settingsDialog = settingsGenerator.Dialog;
    }

    public void Dispose()
    {
        // Order matters: executor.Dispose() runs first to fulfill inflight WorkItem promises,
        // letting HandleToolsCall wake up and write responses while the listener is still alive.
        // The user-script cleanup that follows gives those responses time to flush out.
        // The listener is intentionally NOT stopped first because HttpListener.Stop/Close also
        // cuts off OutputStreams of inflight contexts, which would silence the shutdown responses.
        // Late Enqueue calls that race with Dispose are handled by the double-check inside Enqueue.
        executor?.Dispose();
        mcpServer?.Dispose();
        executor = null;
        mcpServer = null;
    }

    public void Update()
    {
        if (RefreshSettings)
        {
            RefreshSettings = false;
            _settingsDialog?.RecreateControls(false);
        }
        if (executor == null) return;
        if (!executor.Initialized)
            executor.Initialize();
        executor.Tick();
    }

    [UsedImplicitly]
    public void OpenConfigDialog()
    {
        settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(settingsGenerator.Dialog);
    }
}
