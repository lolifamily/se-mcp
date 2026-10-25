using System.Xml.Serialization;
using Avalonia;
using Avalonia.Input.Platform;
using Client2Plugin.Settings.Elements;
using Shared.Mcp;
using JetBrains.Annotations;

namespace Client2Plugin;

// The settings screen's side of the config, as ClientPlugin.Config is on SE1: overrides that carry
// its attributes (Avalonia-bound, out of Shared's reach), Title, the buttons, and Current, the
// instance SettingsGenerator reads. The rest is Shared.Config.PluginConfig.
public class Config : Shared.Config.PluginConfig
{
    [XmlIgnore]
    public string Title => BoundPort > 0 ? $"SeMcp — :{BoundPort}"
                         : ErrorMessage != null ? $"SeMcp — {ErrorMessage}"
                         : "SeMcp — starting…";

    [Separator("MCP Server (port change requires restart)")]
    [Textbox(description: "HTTP port for the MCP server (requires restart)")]
    public override string Port
    {
        get => base.Port;
        set => base.Port = value;
    }

    [Textbox(description: "Secret key (auto-generated if left empty)")]
    public override string SecretKey
    {
        get => base.SecretKey;
        set
        {
            // Empty makes the base generate a token, which the screen then has to show.
            var generated = string.IsNullOrWhiteSpace(value);
            base.SecretKey = value;
            if (generated) Plugin.Instance?.RefreshSettingsDialog();
        }
    }

    // Instance methods, unlike SE1's, as in the template's own example: SE2's SettingsGenerator binds
    // each button to Config.Current, and a static one throws ArgumentException there, which keeps the
    // settings screen from opening. `this` is Config.Current.
    [Button(label: "Regenerate Token", description: "Generate a new secret key (takes effect immediately)")]
    [UsedImplicitly]
    public void RegenerateToken()
    {
        // A non-empty token, so the setter won't refresh the screen itself.
        SecretKey = TokenGenerator.Generate();
        Plugin.Instance?.RefreshSettingsDialog();
    }

    [Button(label: "Copy URL", description: "Copy MCP connection URL to clipboard")]
    [UsedImplicitly]
    public void CopyUrl()
    {
        if (BoundPort <= 0) return;
        var url = $"http://localhost:{BoundPort}/?token={SecretKey}";
        // SE2 has no MyClipboardHelper; Avalonia's IClipboard is registered app-wide. Set without
        // awaiting, from the UI thread.
        var clipboard = AvaloniaLocator.Current.GetService<IClipboard>();
        _ = clipboard?.SetTextAsync(url);
    }

    // A placeholder until Plugin's constructor puts the loaded config here.
    public static Config Current { get; internal set; } = new();
}
