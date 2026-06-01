using System;
using System.Runtime.CompilerServices;

namespace ClientPlugin;

public class ScriptTimeoutException()
    : Exception("Script killed: 1s/frame budget exhausted");

public class ScriptStackException()
    : Exception("Script killed: stack depth budget exhausted (700KB)");

public static class ScriptGuard
{
    // Set true by either the background deadline task in Executor.Tick after
    // FrameTimeoutMs, or by StackCheck when SP descends past the budget.
    // Reset to false at the top of each Executor.Tick.
    public static volatile bool Dead;

    // First StackCheck per script captures the current SP here. Subsequent
    // checks compare SP against it. 0 = unset; Executor.Tick resets it to 0
    // before each script's MoveNext so every script gets its own baseline.
    public static long StackBase;

    // 700KB of SE's 1.5MB main thread stack is the user's recursion budget.
    // The remaining ~800KB covers frames the game/MCP already consumed when
    // StackCheck first samples the baseline (~10KB), CLR system stack use,
    // and a comfortable margin. Filter handlers keep unwind stackless, so
    // the throw path itself barely uses stack.
    private const long StackBudgetBytes = 700_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail()
    {
        if (Dead) throw new ScriptTimeoutException();
    }

    // Injected before every REPL / Delegate call site. Stack grows downward,
    // so deeper frames have smaller SP values. StackBase - sp = bytes consumed.
    // First call per script captures StackBase; subsequent calls throw once
    // SP has descended past the budget.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void StackCheck()
    {
        int marker;
        var sp = (long)&marker;
        if (StackBase == 0) StackBase = sp;
        if (StackBase - sp <= StackBudgetBytes) return;
        throw new ScriptStackException();
    }
}
