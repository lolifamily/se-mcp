using System.Xml.Serialization;
using Avalonia;
using Avalonia.Input.Platform;
using Client2Plugin.Settings.Elements;
using Shared.Mcp;
using JetBrains.Annotations;

namespace Client2Plugin;

// SE2 client-side IPluginConfig: inherits Shared.Config.PluginConfig so the runtime
// fields (Port / SecretKey / BoundPort / ErrorMessage / Denied) and the
// INotifyPropertyChanged plumbing live in exactly one place. The subclass only adds
// what SE2's Avalonia settings model forces to live on the declaring class:
//   - [Textbox] / [Separator] / [Button] attributes on the Port / SecretKey overrides
//     — property-level attributes can only be attached on the declaring class, and
//     Shared can't carry them (would pull Avalonia into the shared/server build).
//   - The token-refresh hook on SecretKey generation (re-renders the settings dialog
//     so the freshly-minted token visibly populates the Textbox).
//   - Title — computed from BoundPort/ErrorMessage, drives the settings dialog header.
//   - Static Current — SettingsGenerator dereferences Config.Current directly.
//   - [Button] RegenerateToken / CopyUrl actions (instance methods — SE2's SettingsGenerator
//     binds them to Config.Current, so static won't work).
//
// This mirrors SE1's ClientPlugin.Config one-for-one; only the GUI mechanics differ
// (Avalonia attributes + IClipboard + close/reopen refresh, vs MyGui + MyClipboardHelper
// + per-frame RecreateControls).
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
        set => base.Port = value;
    }

    [Textbox(description: "Secret key (auto-generated if left empty)")]
    public override string SecretKey
    {
        get => base.SecretKey;
        set
        {
            // base.SecretKey's setter handles the empty → TokenGenerator.Generate()
            // policy + SetValue + PropertyChanged. Detect empty BEFORE the base call
            // (base fills it in) so we can re-render the dialog and show the new token.
            var generated = string.IsNullOrWhiteSpace(value);
            base.SecretKey = value;
            if (generated) Plugin.Instance?.RefreshSettingsDialog();
        }
    }

    // Instance methods (NOT static). SE2's SettingsGenerator binds button delegates via
    // CreateDelegate(delegateType, Config.Current, method) — a STATIC method's signature is
    // incompatible with that instance-bound delegate and throws ArgumentException, which is
    // exactly what broke OpenConfigDialog. So `this` here IS Config.Current; use its members
    // directly. (SE1's MyGui SettingsGenerator supported static buttons; SE2's Avalonia one
    // does not — the template demo's [Button] is likewise an instance method.)
    [Button(label: "Regenerate Token", description: "Generate a new secret key (takes effect immediately)")]
    [UsedImplicitly]
    public void RegenerateToken()
    {
        // Generate() is non-empty, so the setter's generated-branch won't fire — refresh here.
        SecretKey = TokenGenerator.Generate();
        Plugin.Instance?.RefreshSettingsDialog();
    }

    [Button(label: "Copy URL", description: "Copy MCP connection URL to clipboard")]
    [UsedImplicitly]
    public void CopyUrl()
    {
        if (BoundPort <= 0) return;
        var url = $"http://localhost:{BoundPort}/?token={SecretKey}";
        // SE2 has no static MyClipboardHelper equivalent; Avalonia's IClipboard is bound
        // app-wide in the AvaloniaLocator during AvaloniaApp init, reachable from this button
        // action. Fire-and-forget the async set (we're on the UI thread).
        var clipboard = AvaloniaLocator.Current.GetService<IClipboard>();
        _ = clipboard?.SetTextAsync(url);
    }

    // Current starts at a throwaway default instance and is re-bound by Plugin's ctor to the
    // PersistentConfig wrapper's Data, so GUI mutations route through the live config and
    // trigger the 500ms auto-save. SettingsGenerator dereferences Config.Current directly.
    public static Config Current { get; internal set; } = new();
}
