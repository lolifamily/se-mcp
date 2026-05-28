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
    internal static SettingsScreen SettingsDialog;
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
        SettingsDialog = settingsGenerator.Dialog;
    }

    public void Dispose()
    {
        mcpServer?.Dispose();
        mcpServer = null;
        executor = null;
    }

    public void Update()
    {
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
