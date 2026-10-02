using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Shared.Mcp;

// The tools/list entry every tool shares — name, description, input schema — written once, as the
// Minecraft MCP's McpJson.tool does: what differs between two tools is their prose. enum, default and
// required are schema fields, so the prose never has to name them.
//
// Utf8JsonWriter and the relaxed encoder are in the System.Text.Json the net48 builds compile against —
// 8.0.7, shipped in both Bin64 and DedicatedServer64 — and at run time they bind to that same file.
internal static class ToolSchema
{
    // One parameter. type "array" is an array of strings (usings, the only one); default is a string or
    // a bool. A class, not a record: a record's init accessors need IsExternalInit, which net48 lacks.
    internal sealed class Param(
        string name, string description, string type = "string",
        bool required = false, string[] @enum = null, object @default = null)
    {
        public readonly string Name = name;
        public readonly string Description = description;
        public readonly string Type = type;
        public readonly bool Required = required;
        public readonly string[] Enum = @enum;
        public readonly object Default = @default;
    }

    // Relaxed escaping: every string here is ours and ends up in a JSON parser, never in HTML — so <, >,
    // ' and + stay as written instead of going out as unicode escapes.
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Build(string name, string description, params Param[] parameters)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("description", description);
            w.WriteStartObject("inputSchema");
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            foreach (var p in parameters)
                WriteParam(w, p);
            w.WriteEndObject();
            // Absent rather than empty when nothing is required: older JSON Schema drafts reject [].
            if (parameters.Any(p => p.Required))
            {
                w.WriteStartArray("required");
                foreach (var p in parameters.Where(p => p.Required))
                    w.WriteStringValue(p.Name);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteParam(Utf8JsonWriter w, Param p)
    {
        w.WriteStartObject(p.Name);
        w.WriteString("type", p.Type);
        if (p.Type == "array")
        {
            w.WriteStartObject("items");
            w.WriteString("type", "string");
            w.WriteEndObject();
        }
        if (p.Enum != null)
        {
            w.WriteStartArray("enum");
            foreach (var value in p.Enum)
                w.WriteStringValue(value);
            w.WriteEndArray();
        }
        switch (p.Default)
        {
            case bool b: w.WriteBoolean("default", b); break;
            case string s: w.WriteString("default", s); break;
        }
        w.WriteString("description", p.Description);
        w.WriteEndObject();
    }
}
