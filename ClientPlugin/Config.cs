using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using JetBrains.Annotations;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    [XmlIgnore]
    public string Status;
    [XmlIgnore]
    public string Title => Status != null ? $"SeMcp — {Status}" : "SeMcp";

    [Separator("MCP Server (changes require restart)")]

    [Textbox(description: "HTTP port for the MCP server")]
    public string Port
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = "9876";

    [Textbox(description: "Secret key for authentication (empty = no auth)")]
    public string SecretKey
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = "";

    #region Property change notification boilerplate

    public static readonly Config Default = new();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    #endregion
}
