using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Shared.Mcp;

// Thrown into a step the watchdog is killing. Only ever shown under Executor.TimeoutReport's header,
// which already says the step was cut and what the budget was, so the message is the next move alone.
public class ScriptTimeoutException()
    : Exception("split work across frames with `yield return null`");

// Thrown by StackCheck. Shown under "script threw:", where the folded stack shows the recursion.
public class ScriptStackException()
    : Exception("stack depth budget exhausted (700KB)");

// What the checks Compiler.InjectGuards writes into every script method work from. Its kill test reads
// KillId and LaneThread straight from the script's IL, at method entry, on backward branches, at
// finally/fault entry and in the filters catch blocks are rewritten into: a Debug-compiled script is
// never inlined into, AggressiveInlining or not, and a call on every backward branch made a tight loop
// about four times slower. Public for that IL. StackCheck runs once per call, at method entry, and
// stays a method. One class per frame lane, main and render, rather than one class with parallel
// fields: Compiler is handed the class's own pre-resolved members (KillIdField etc.), so each lane's
// KillId is written only by its own watchdog and read only by its own scripts. The two classes are
// identical.
//
// KillId is the id of the script to kill on the lane, or 0. Every check compares it with the id baked
// into its own script, so a script answers only to a kill aimed at itself (see FrameWatchdog for why
// an id and not a flag), and only on the lane thread. The kill exists to free that thread. The same
// script's code running elsewhere at that moment (a thread it started, a timer, a finalizer, a Harmony
// patch the game runs on another thread) is left alone: thrown there, the exception lands in code that
// never expected it, and at the top of a thread that ends the process.

public static class ScriptGuardMain
{
    // Plain static volatile, not thread-static: one of its writers is the watchdog's timer thread.
    public static volatile int KillId;

    // The thread this lane steps scripts on, the only one a kill fires on. BeginStep writes it before
    // every step, so it is in place before any kill in that step can be raised.
    public static volatile int LaneThread;

    // The stack pointer at the step's first check, 0 before it. Thread-static: the script that sets it
    // and the StackCheck that reads it are always on the same thread.
    [ThreadStatic] private static long _stackBase;

    public static readonly FieldInfo KillIdField = typeof(ScriptGuardMain).GetField(nameof(KillId));
    public static readonly FieldInfo LaneThreadField = typeof(ScriptGuardMain).GetField(nameof(LaneThread));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardMain).GetMethod(nameof(StackCheck));

    private const long StackBudgetBytes = 700_000;

    // Lane thread, just before each step: a fresh stack baseline for the script about to run, and this
    // thread as the one a kill is aimed at.
    public static void BeginStep()
    {
        _stackBase = 0;
        LaneThread = Environment.CurrentManagedThreadId;
    }

    // Throws once the step's stack has grown past the budget. A stack overflow can't be caught in .NET
    // and takes the process with it, so runaway recursion has to stop here first.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void StackCheck()
    {
        int marker;
        var sp = (long)&marker;
        if (_stackBase == 0) _stackBase = sp;
        if (_stackBase - sp <= StackBudgetBytes) return;
        throw new ScriptStackException();
    }
}

public static class ScriptGuardRender
{
    // See ScriptGuardMain.
    public static volatile int KillId;

    public static volatile int LaneThread;

    [ThreadStatic] private static long _stackBase;

    public static readonly FieldInfo KillIdField = typeof(ScriptGuardRender).GetField(nameof(KillId));
    public static readonly FieldInfo LaneThreadField = typeof(ScriptGuardRender).GetField(nameof(LaneThread));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardRender).GetMethod(nameof(StackCheck));

    private const long StackBudgetBytes = 700_000;

    public static void BeginStep()
    {
        _stackBase = 0;
        LaneThread = Environment.CurrentManagedThreadId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void StackCheck()
    {
        int marker;
        var sp = (long)&marker;
        if (_stackBase == 0) _stackBase = sp;
        if (_stackBase - sp <= StackBudgetBytes) return;
        throw new ScriptStackException();
    }
}
