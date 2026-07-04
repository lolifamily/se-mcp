using System.Xml.Serialization;
using ClientPlugin.Settings.Elements;
using JetBrains.Annotations;
using Shared.Mcp;
using VRage.Utils;

namespace ClientPlugin;

// Client-side IPluginConfig: inherits Shared.Config.PluginConfig so the runtime
// fields (Port / SecretKey / BoundPort / ErrorMessage / Enabled) and the
// INotifyPropertyChanged plumbing live in exactly one place. The subclass only
// adds the things SE's GUI model forces to live here:
//   - Settings GUI attributes ([Textbox] / [Separator] / [Button]) on the
//     Port / SecretKey overrides — property-level attributes can only be
//     attached on the declaring class, and Shared can't carry them (would pull
//     ClientPlugin.Settings into the server build).
//   - The RefreshSettings hook on SecretKey auto-generation (client-only,
//     re-renders the Settings dialog so the freshly-minted token visibly
//     populates the Textbox).
//   - Title — computed from BoundPort/ErrorMessage, drives the Settings dialog header.
//   - Static Current — required by SE's SettingsGenerator framework, which
//     dereferences Config.Current directly (SettingsGenerator.cs:48/114/115).
//   - Static [Button] RegenerateToken / CopyUrl actions.
//
// XmlSerializer descends through Config and picks up the base class's public
// properties automatically; [XmlIgnore] on the base BoundPort / ErrorMessage stays in
// effect. The .cfg root element stays <Config>, matching the SeMcp 0.x layout
// so existing users' stored tokens load unchanged.
public class Config : Shared.Config.PluginConfig
{
    [XmlIgnore]
    public string Title => BoundPort > 0  ? $"SeMcp — :{BoundPort}"
                         : ErrorMessage != null  ? $"SeMcp — {ErrorMessage}"
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
            // base.SecretKey's setter handles the empty → TokenGenerator.Generate()
            // policy + SetValue + PropertyChanged. The GUI-side detail — re-render
            // the Settings dialog so the freshly-minted token visibly populates
            // the Textbox — is appended here. Detecting empty BEFORE the base
            // call avoids re-reading the now-generated string back from base.SecretKey.
            var generated = string.IsNullOrWhiteSpace(value);
            base.SecretKey = value;
            if (generated) Plugin.RefreshSettings = true;
        }
    }

    [Button(label: "Regenerate Token", description: "Generate a new secret key (takes effect immediately)")]
    [UsedImplicitly]
    public static void RegenerateToken()
    {
        // SecretKey setter fires PropertyChanged → PersistentConfig schedules the 500ms
        // auto-save (and a final synchronous Save in Dispose). No manual save needed.
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

    // Current starts at a throwaway default instance and is re-bound by Plugin.Init to the
    // PersistentConfig wrapper's Data once loaded; setting it through the wrapper means GUI
    // mutations route through the live config and trigger the 500ms auto-save.
    public static Config Current { get; internal set; } = new();
}
