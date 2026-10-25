using System.Xml.Serialization;
using ClientPlugin.Settings.Elements;
using JetBrains.Annotations;
using Shared.Mcp;
using VRage.Utils;

namespace ClientPlugin;

// The settings dialog's side of the config: overrides that carry its attributes (ClientPlugin's own
// types, out of Shared's reach), Title, the buttons, and Current, the instance SettingsGenerator
// reads. The rest is Shared.Config.PluginConfig.
//
// The .cfg's root element is still <Config>, as in SeMcp 0.x, so existing files load.
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
        [UsedImplicitly]
        set => base.Port = value;
    }

    [Textbox(description: "Secret key (auto-generated if left empty)")]
    public override string SecretKey
    {
        get => base.SecretKey;
        [UsedImplicitly]
        set
        {
            // Empty makes the base generate a token, which the dialog then has to show.
            var generated = string.IsNullOrWhiteSpace(value);
            base.SecretKey = value;
            if (generated) Plugin.RefreshSettings = true;
        }
    }

    [Button(label: "Regenerate Token", description: "Generate a new secret key (takes effect immediately)")]
    [UsedImplicitly]
    public static void RegenerateToken()
    {
        // A non-empty token, so the setter won't refresh the dialog itself.
        Current.SecretKey = TokenGenerator.Generate();
        Plugin.RefreshSettings = true;
    }

    [Button(label: "Copy URL", description: "Copy MCP connection URL to clipboard")]
    [UsedImplicitly]
    public static void CopyUrl()
    {
        if (Current.BoundPort <= 0) return;
        MyClipboardHelper.SetClipboard($"http://localhost:{Current.BoundPort}/?token={Current.SecretKey}");
    }

    // A placeholder until Plugin.Init puts the loaded config here.
    public static Config Current { get; internal set; } = new();
}
