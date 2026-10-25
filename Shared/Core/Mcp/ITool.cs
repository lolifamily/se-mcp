using System.Text.Json;

namespace Shared.Mcp;

// One MCP tool: its tools/list entry, and how its tools/call arguments become a WorkItem for whatever
// runs it (a lane, the screenshot service). McpServer handles the JSON-RPC around it.
public interface ITool
{
    string Name { get; }

    // The tool's tools/list entry: {"name":...,"description":...,"inputSchema":{...}}.
    string SchemaJson { get; }

    // Whether a successful result is the image file whose path is in item.Output (take_screenshot)
    // rather than text. Declared, not guessed from the output: a path is text too.
    bool ReturnsImage { get; }

    // Parses the arguments and hands the item on, after which McpServer waits on item.Done. On false
    // nothing was dispatched, and errorCode and errorMessage make the JSON-RPC error. item.Cancel is
    // set already.
    bool TryDispatch(JsonElement arguments, WorkItem item, out int errorCode, out string errorMessage);
}
