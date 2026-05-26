using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
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
    private SettingsGenerator settingsGenerator;
    private Executor executor;
    private McpServer mcpServer;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        executor = new Executor();
        mcpServer = new McpServer(executor, Config.Current.Port, Config.Current.SecretKey);
        mcpServer.Start();

        settingsGenerator = new SettingsGenerator();
    }

    public void Dispose()
    {
        mcpServer?.Dispose();
        mcpServer = null;
        executor = null;
    }

    public void Update()
    {
        executor?.Tick();
    }

    [UsedImplicitly]
    public void OpenConfigDialog()
    {
        settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(settingsGenerator.Dialog);
    }
}
