using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Serialization;
using Shared.Mcp;

namespace Shared.Config;

// The config both clients share, SE1's and SE2's: what McpServer reads, with change notification,
// so PersistentConfig saves each change as it happens. In the settings dialog an edit is therefore
// saved at once, and no Cancel takes it back: accepted, so that every host saves the same way, the
// server included. A client subclasses it to put its settings attributes on Port and SecretKey,
// hence the virtuals. The server has its own, ServerPlugin.Config.
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

    // Empty until the plugin puts its default here, which is then saved. The defaults keep each
    // host's ten ports apart, so all can run at once: SE1 9876, SE1's server 9000, SE2 6789. An
    // override forwards to this one, which keeps the value and raises the change.
    public virtual string Port
    {
        get;
        set => SetValue(ref field, value);
    } = "";

    // Empty becomes a fresh token, saved like any other change.
    public virtual string SecretKey
    {
        get;
        set
        {
            var generated = string.IsNullOrWhiteSpace(value);
            SetValue(ref field, generated ? TokenGenerator.Generate() : value);
        }
    } = "";

    // Never saved. BoundPort is the port McpServer bound, Port or one of the next nine; ErrorMessage
    // says why it bound none, for the settings dialog.
    [XmlIgnore] public int BoundPort { get; set; }
    [XmlIgnore] public string ErrorMessage { get; set; }

    // Written by the main-lane pump, read from any thread. Volatile by hand, as a property can't be
    // `volatile`: atomicity alone orders nothing under ECMA-335, and x86 merely hides that.
    [XmlIgnore]
    public bool Denied
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}