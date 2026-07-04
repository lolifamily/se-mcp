using System.Xml.Serialization;
using PluginSdk.Config;
using Shared.Config;
using Shared.Mcp;

namespace ServerPlugin;

[Section("status", caption: "SeMcp")]
public class Config : PluginSdk.Config.PluginConfig, IPluginConfig
{
    internal static Config Instance;

    internal static string StatusCaption =>
        Instance?.BoundPort > 0 ? $"SeMcp — :{Instance.BoundPort}" :
        Instance?.ErrorMessage != null ? $"SeMcp — {Instance.ErrorMessage}" :
        "SeMcp — starting…";

    [StringOption(description: "HTTP port (requires restart)", Parent = "status")]
    public string Port
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [StringOption(description: "Bearer token (auto-generated if empty)", Parent = "status")]
    public string SecretKey
    {
        get;
        set
        {
            var generated = string.IsNullOrWhiteSpace(value);
            SetField(ref field, generated ? TokenGenerator.Generate() : value);
        }
    } = "";

    [XmlIgnore] public int BoundPort { get; set; }
    [XmlIgnore] public string ErrorMessage { get; set; }

    // Server is always authoritative — it can't be a pure client behind someone
    // else's deny-gate, so there's nothing to refresh and nothing to store. The
    // gate is a constant here; the interface only needs the read side.
    [XmlIgnore]
    public bool Denied => false;
}
