using System.Text.Json;

namespace Shared.Mcp;

// One tool = one MCP tools/list entry + the parse-and-dispatch logic that turns its
// tools/call arguments into a WorkItem on some lane. McpServer stays generic: it
// validates the JSON-RPC envelope, the tool resolves params.arguments and decides
// which Executor (or screenshot service) gets the WorkItem.
//
// Why ReturnsImage lives on the tool (not deduced from item.Output):
//   take_screenshot writes a path to item.Output; the path-vs-text dispatch needs to
//   pick the JSON content array shape BEFORE inspecting that string. A flag-on-tool
//   keeps the choice declarative.
public interface ITool
{
    string Name { get; }

    // Full tools/list object JSON: {"name":"...","description":"...","inputSchema":{...}}
    // McpServer concatenates these with commas and wraps in {"tools":[...]}.
    string SchemaJson { get; }

    // True for tools whose successful result encodes the on-disk file at item.Output
    // as an image in the JSON-RPC response (take_screenshot). False for text results
    // (execute_code).
    bool ReturnsImage { get; }

    // Parse params.arguments and dispatch to a lane. Returns:
    //   true  — item has been Enqueue'd / Begin'd; caller awaits item.Done.
    //   false — errorCode + error are populated; caller emits a JSON-RPC error.
    // Pre-condition: item.Cancel is already set by the caller.
    bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string error);
}
