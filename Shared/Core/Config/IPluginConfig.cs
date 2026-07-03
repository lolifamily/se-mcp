using System.ComponentModel;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // MCP server config — same surface on client (GUI-driven Config) and server (PersistentConfig<PluginConfig>).
    // - Port: HTTP port string from settings (may be missing/invalid; McpServer parses with fallback 9876).
    // - SecretKey: Bearer token; must be non-empty before Start (client seeds via TokenGenerator on first load).
    // - BoundPort: writable, set by McpServer after a successful Bind; Host-header check reads it back.
    // - Error: writable, set by McpServer on bind failure for UI display.
    string Port { get; }
    string SecretKey { get; }
    int BoundPort { get; set; }
    string Error { get; set; }

    // Whether code execution should be denied. A plain stored bool, NOT a
    // computed property. The owner thread (client: Plugin.Update on main;
    // server: never writes) refreshes this once per frame from whatever SE
    // state it consults (e.g. MyAPIGateway.Session.PromoteLevel < Admin);
    // any thread may read it — bool reads/writes are atomic on .NET, and
    // up to one frame of staleness is acceptable since Update rewrites
    // each frame. No volatile / Interlocked needed.
    //
    // Interface exposes only the read side — McpServer / Executor only need
    // the gate value. The write lives on the implementing class (PluginConfig
    // declares `set`), and the only writer (client Plugin.Update) holds a
    // concrete Config reference, so going through the interface for writes
    // is never required.
    bool Denied { get; }
}