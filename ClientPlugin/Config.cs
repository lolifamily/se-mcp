using System;
using System.Security.Cryptography;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using JetBrains.Annotations;
using VRage.Utils;

namespace ClientPlugin;

public class Config : INotifyPropertyChanged
{
    [XmlIgnore]
    public int BoundPort;
    [XmlIgnore]
    public string Error;
    [XmlIgnore]
    public string Title => BoundPort > 0  ? $"SeMcp — :{BoundPort}"
                         : Error != null  ? $"SeMcp — {Error}"
                         : "SeMcp — starting…";

    [Separator("MCP Server (port change requires restart)")]

    [Textbox(description: "HTTP port for the MCP server (requires restart)")]
    public string Port
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = "9876";

    [Textbox(description: "Secret key (auto-generated if left empty)")]
    public string SecretKey
    {
        get;
        [UsedImplicitly]
        set
        {
            var generated = string.IsNullOrWhiteSpace(value);
            SetField(ref field, generated ? GenerateToken() : value);
            if (generated) Plugin.RefreshSettings = true;
        }
    } = "";

    [Button(label: "Regenerate Token", description: "Generate a new secret key (takes effect immediately)")]
    [UsedImplicitly]
    public static void RegenerateToken()
    {
        Current.SecretKey = GenerateToken();
        ConfigStorage.Save(Current);
        Plugin.RefreshSettings = true;
    }

    [Button(label: "Copy URL", description: "Copy MCP connection URL to clipboard")]
    [UsedImplicitly]
    public static void CopyUrl()
    {
        if (Current.BoundPort <= 0) return;
        MyClipboardHelper.SetClipboard($"http://localhost:{Current.BoundPort}/?token={Current.SecretKey}");
    }

    internal static string GenerateToken()
    {
        var bytes = new byte[16];
        using (var rng = new RNGCryptoServiceProvider())
            rng.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

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
