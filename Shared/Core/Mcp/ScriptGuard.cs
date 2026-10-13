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

// The checks Compiler.InjectGuards writes into every script method: StackCheck and Bail at method
// entry, Bail on backward branches and at finally/fault entry, and Killing in the filters that catch
// blocks are rewritten into. One class per frame lane, main and render, rather than one class with
// parallel fields: Compiler injects the class's own pre-resolved methods (BailMethod etc.), so each
// lane's KillId is written only by its own watchdog and read only by its own scripts. The two classes
// are identical.
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
    internal static volatile int KillId;

    // The thread this lane steps scripts on, the only one Killing says yes on. BeginStep writes it
    // before every step, so it is in place before any kill in that step can be raised.
    private static volatile int _laneThread;

    // The stack pointer at the step's first check, 0 before it. Thread-static: the script that sets it
    // and the StackCheck that reads it are always on the same thread.
    [ThreadStatic] private static long _stackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardMain).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardMain).GetMethod(nameof(StackCheck));
    public static readonly MethodInfo KillingMethod = typeof(ScriptGuardMain).GetMethod(nameof(Killing));

    private const long StackBudgetBytes = 700_000;

    // Lane thread, just before each step: a fresh stack baseline for the script about to run, and this
    // thread as the one a kill is aimed at.
    public static void BeginStep()
    {
        _stackBase = 0;
        _laneThread = Environment.CurrentManagedThreadId;
    }

    // Is this script being killed, here? The thread test runs only once the id matches, so outside a
    // kill it costs nothing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Killing(int scriptId) =>
        KillId == scriptId && Environment.CurrentManagedThreadId == _laneThread;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail(int scriptId)
    {
        if (Killing(scriptId)) throw new ScriptTimeoutException();
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
    // Written by the render Executor's setKillId lambda, which the compiler can't see from here. A host
    // with no render lane (the dedicated server) never writes it, so its build would warn CS0649.
#pragma warning disable CS0649
    internal static volatile int KillId;
#pragma warning restore CS0649

    // See ScriptGuardMain.
    private static volatile int _laneThread;

    [ThreadStatic] private static long _stackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardRender).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardRender).GetMethod(nameof(StackCheck));
    public static readonly MethodInfo KillingMethod = typeof(ScriptGuardRender).GetMethod(nameof(Killing));

    private const long StackBudgetBytes = 700_000;

    public static void BeginStep()
    {
        _stackBase = 0;
        _laneThread = Environment.CurrentManagedThreadId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Killing(int scriptId) =>
        KillId == scriptId && Environment.CurrentManagedThreadId == _laneThread;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail(int scriptId)
    {
        if (Killing(scriptId)) throw new ScriptTimeoutException();
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
