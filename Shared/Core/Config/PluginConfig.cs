using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Serialization;
using Shared.Mcp;

namespace Shared.Config;

// Default implementation of IPluginConfig — the shared runtime config surface
// the McpServer reads. Two hosts persist it the same way but the client adds
// a GUI layer:
//
//   - Server: PersistentConfig<PluginConfig> (XML, 500ms auto-save on
//             PropertyChanged). Headless, no "OK button" moment, so
//             PropertyChanged == intent-to-commit. Auto-save is exactly right.
//
//   - Client: PersistentConfig<Config> too — same 500ms auto-save. Edits
//             commit on PropertyChanged, NOT at OK-button time, so SE GUI's
//             "Cancel to discard" semantics do not hold here. Accepted to
//             share one persistence model with Server. SettingsScreen.OnRemoved
//             still calls ConfigStorage.Save as a redundant final flush
//             (harmless: same file, same XmlSerializer<Config>).
//             The ClientPlugin.Config subclass keeps the SEMCP-legacy storage
//             path (MyFileSystem.UserDataPath\Storage\SeMcp.cfg) for back-compat
//             with existing SeMcp users' stored tokens, AND attaches the
//             [Textbox] / [Button] GUI attributes — that's why Port/SecretKey
//             below are virtual.
public class PluginConfig : IPluginConfig
{
    public event PropertyChangedEventHandler PropertyChanged;

    private void SetValue<T>(ref T target, T value, [CallerMemberName] string propName = "")
    {
        if (EqualityComparer<T>.Default.Equals(target, value))
            return;
        target = value;
        OnPropertyChanged(propName);
    }

    private void OnPropertyChanged([CallerMemberName] string propName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    // virtual: client subclass overrides to attach [Textbox] GUI attribute. The
    // backing storage + change notification stay here — override forwards via
    // base.Port = value so there's exactly one source of truth.
    //
    // Default is empty: the host-specific default port (9876 on the client,
    // 9000 on the server — disjoint from the client's 10-port retry range
    // 9876-9885 so both can run side-by-side during development) is injected
    // in Plugin.Init when this is found empty, then persisted to .cfg. Empty
    // here means "no preference yet, ask the host". Same shape as SecretKey:
    // empty → mint default → save.
    public virtual string Port
    {
        get;
        set => SetValue(ref field, value);
    } = "";

    // Empty SecretKey → mint a fresh token through this setter. Server: the
    // PersistentConfig auto-save tick (500ms) flushes the generated token to
    // disk; subsequent restarts keep it. Client: ConfigStorage.Save runs after
    // Plugin.Init; same effect. virtual for the GUI attribute + RefreshSettings
    // trigger on the client subclass.
    public virtual string SecretKey
    {
        get;
        set
        {
            var generated = string.IsNullOrWhiteSpace(value);
            SetValue(ref field, generated ? TokenGenerator.Generate() : value);
        }
    } = "";

    // Runtime-only — never persisted. BoundPort is what McpServer actually bound
    // (basePort..basePort+retries-1); Error captures bind failure for UI display.
    // Denied is refreshed each frame by the owner-thread Update on hosts that
    // gate execution (client SE Admin check); server never writes it, stays false.
    [XmlIgnore] public int BoundPort { get; set; }
    [XmlIgnore] public string Error { get; set; }

    // Volatile.Read/Write on the field-backed property: main thread (client
    // Plugin.Update) writes, render thread (RenderExecutor.Tick) reads. bool
    // reads/writes are atomic on .NET but that alone gives no memory-ordering
    // guarantee under ECMA-335; x86/x64's strong memory model masks the
    // omission in practice. Explicit fences here so we don't depend on that.
    // `volatile` can't decorate a property; this is the property-level analog.
    [XmlIgnore]
    public bool Denied
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}