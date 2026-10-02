using System.IO;
using System.Text;

namespace Shared.Mcp;

// A script's output: accumulate, take once, seal — the Minecraft MCP's Capture. Every lane hands each
// script one as its Console (CompilationResult.Start).
//
// One lock, held for one append and never while user code runs. What runs user code — ToString,
// IFormattable, string.Format — happens before the gate: the TextWriter base formats first and then
// calls down, and the WriteLine overrides below do the same for the overloads the base would split
// into Write(value) plus WriteLine() (.NET 10 splits WriteLine(string) too). So every call is one
// append, and a WriteLine's text and newline land together — no other thread's text between them.
// Not TextWriter.Synchronized: it makes a whole call atomic by holding its lock across the formatting
// as well, so one slow ToString stalls every other writer, and whoever takes the output behind them.
// java.io.PrintStream moved formatting out of its lock for the same reason (JDK-4905777).
//
// Sealing is the point, not an optimization: code the script leaves behind (a Harmony patch, an event
// handler, a thread it started) still holds this writer — the script's Console holder is static and
// its assembly never unloads — and would write forever into something nobody reads. Take ends that:
// later writes return before the gate, and pin no text.
internal sealed class Capture : TextWriter
{
    private readonly object gate = new();

    // Accumulator and seal flag in one: non-null == open. Volatile so a write can check it without the
    // gate — the path an outliving writer takes for the rest of the process.
    private volatile StringBuilder text = new();

    // \n, not the platform's \r\n: the text goes to a model, and ScriptRender joins on \n.
    public Capture() => NewLine = "\n";

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (text == null) return;
        lock (gate) text?.Append(value);
    }

    public override void Write(string value)
    {
        if (text == null) return;
        lock (gate) text?.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (text == null) return;
        lock (gate) text?.Append(buffer, index, count);
    }

    public override void WriteLine(string value)
    {
        if (text == null) return;
        lock (gate) text?.Append(value).Append(CoreNewLine);
    }

    public override void WriteLine(char value) => WriteLine(value.ToString());

    public override void WriteLine(char[] buffer) => WriteLine(new string(buffer));

    public override void WriteLine(char[] buffer, int index, int count) => WriteLine(new string(buffer, index, count));

    public override void WriteLine(bool value) => WriteLine(value ? "True" : "False");

    public override void WriteLine(int value) => WriteLine(value.ToString(FormatProvider));

    public override void WriteLine(uint value) => WriteLine(value.ToString(FormatProvider));

    public override void WriteLine(long value) => WriteLine(value.ToString(FormatProvider));

    public override void WriteLine(ulong value) => WriteLine(value.ToString(FormatProvider));

    public override void WriteLine(float value) => WriteLine(value.ToString(FormatProvider));

    public override void WriteLine(double value) => WriteLine(value.ToString(FormatProvider));

    public override void WriteLine(decimal value) => WriteLine(value.ToString(FormatProvider));

#if NETCOREAPP
    // The base writes a StringBuilder chunk by chunk.
    public override void Write(StringBuilder value) => Write(value?.ToString());

    public override void WriteLine(StringBuilder value) => WriteLine(value?.ToString());
#endif

    // Everything written so far, sealing the writer: every later write is dropped. Only the seal is
    // done under the gate — the builder is this caller's alone once it is out, so the O(size) copy
    // holds no lock.
    public string Take()
    {
        StringBuilder taken;
        lock (gate)
        {
            taken = text;
            text = null;
        }
        return taken?.ToString() ?? "";
    }
}
