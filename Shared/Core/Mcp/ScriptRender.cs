using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Shared.Mcp;

// The text of an execute_code result: how a script's own output and the report of whatever ended
// it are joined, and how an exception reads. Pure functions over their arguments — ported from the
// Minecraft MCP's EvalRender, so both hosts hand a model the same shapes.
internal static class ScriptRender
{
    // Post-fold line cap for one rendered exception: above any real stack, far below what runaway
    // recursion reaches before StackCheck stops it.
    private const int MaxLines = 128;

    // Longest repeating block FoldRepeats looks for: recursion cycles are short (1 direct, 2 mutual,
    // a handful for a delegation loop).
    private const int MaxCycle = 32;

    // The script's output, then the report of how it ended, one newline apart. Only the output's
    // trailing newlines go — its leading whitespace is the script's own. One newline, not a blank
    // line: a client previews the first lines of an error, and the report has to be among them, or
    // the output's first line reads as the error itself. "(no output)" rather than an empty text,
    // which a client shows as nothing having come back at all.
    public static string Combine(string output, string tail)
    {
        var o = (output ?? "").TrimEnd('\n');
        tail ??= "";
        if (o.Length == 0)
            return tail.Length == 0 ? "(no output)" : tail;
        return tail.Length == 0 ? o : o + "\n" + tail;
    }

    // An exception as .NET prints it — type, message, frames and the whole InnerException chain —
    // with two changes for a reader billed per line: a run of repeated frames collapses to one copy
    // and a count (runaway recursion is thousands of identical frames before StackCheck stops it),
    // and what is left is capped. A TargetInvocationException is peeled first: it is reflection's
    // wrapper around what the script actually threw. Newlines come out as \n, whatever the platform.
    public static string Stack(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: not null } tie)
            ex = tie.InnerException;
        var lines = FoldRepeats(ex.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList());
        if (lines.Count > MaxLines)
        {
            var more = lines.Count - MaxLines;
            lines.RemoveRange(MaxLines, more);
            lines.Add($"   ... {more} more line(s)");
        }
        return string.Join("\n", lines);
    }

    // Consecutive repeats of a block of lines → one copy plus a count. Period-aware, because mutual
    // recursion alternates frames and never puts two equal lines side by side. Lines, not frames:
    // what repeats for the reader is the rendered text.
    private static List<string> FoldRepeats(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var (period, reps) = BestCycle(lines, i);
            if (period == 0)
            {
                result.Add(lines[i++]);
                continue;
            }
            result.AddRange(lines.GetRange(i, period));
            result.Add($"   ... last {period} frame(s) x{reps} ({period * reps} frames)");
            i += period * reps;
        }
        return result;
    }

    // The period covering the most lines from `start` wins, ties to the shortest — so `a a b` twice
    // folds as one 3-line block, not `a` x2 plus a remainder. It must replace more than period + 1
    // lines, or the marker costs what it saves; that subsumes "repeats at least twice".
    private static (int Period, int Reps) BestCycle(List<string> lines, int start)
    {
        int period = 0, reps = 0;
        for (var p = 1; p <= Math.Min(MaxCycle, (lines.Count - start) / 2); p++)
        {
            var r = RepeatsAt(lines, start, p);
            if (r * p > p + 1 && r * p > period * reps)
                (period, reps) = (p, r);
        }
        return (period, reps);
    }

    // How many times the block of p lines at `start` repeats back to back; 1 when it does not.
    private static int RepeatsAt(List<string> lines, int start, int p)
    {
        var n = 1;
        for (var at = start + p; at + p <= lines.Count && SameBlock(lines, start, at, p); at += p)
            n++;
        return n;
    }

    private static bool SameBlock(List<string> lines, int a, int b, int length)
    {
        for (var k = 0; k < length; k++)
        {
            if (lines[a + k] != lines[b + k])
                return false;
        }
        return true;
    }
}
