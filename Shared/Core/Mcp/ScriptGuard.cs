using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Shared.Mcp;

// Only ever shown under Executor.TimeoutReport's header, which already says the step was cut and
// what the budget was — so the message is the next move alone.
public class ScriptTimeoutException()
    : Exception("split work across frames with `yield return null`");

// Shown under "script threw:"; the folded stack under it shows the recursion.
public class ScriptStackException()
    : Exception("stack depth budget exhausted (700KB)");

// Two independent guards — one per execution lane (main / render).
// Kept as separate static classes rather than one class with parallel
// fields. Compiler picks which to inject via the pre-resolved Bail/
// StackCheck/Killing handles each class exposes (BailMethod etc.), so
// each lane's KillId is touched only by its own Executor's watchdog
// and read only by its own scripts. _stackBase is [ThreadStatic] because
// the writer (the script) and reader (StackCheck) are always the same
// thread; KillId is plain static volatile because one of its writers is
// the watchdog's timer thread, crossing into the executing thread.
//
// KillId is the id of the script to kill on this lane, or 0 for none.
// Every Bail call and catch filter asks Killing with the id baked into
// its own script at compile time, so a script only ever answers to a kill
// aimed at itself — see FrameWatchdog for why an id and not a flag — and
// only on the lane's own thread. The kill exists to free that thread. The
// same script's code running elsewhere at that moment (a thread it
// started, a timer, a finalizer, a Harmony patch the game runs on another
// thread) is left alone: thrown there, the exception lands in code that
// never expected it, and at the top of a thread that ends the process.

public static class ScriptGuardMain
{
    internal static volatile int KillId;

    // The thread this lane steps its scripts on — the only one Killing says
    // yes on. BeginStep writes it before every step, so it is in place before
    // any kill in that step can be raised.
    private static volatile int _laneThread;

    [ThreadStatic] private static long _stackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardMain).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardMain).GetMethod(nameof(StackCheck));
    public static readonly MethodInfo KillingMethod = typeof(ScriptGuardMain).GetMethod(nameof(Killing));

    private const long StackBudgetBytes = 700_000;

    // Lane thread, just before each step: a fresh stack baseline for the
    // script about to run, and this thread as the one a kill is aimed at.
    public static void BeginStep()
    {
        _stackBase = 0;
        _laneThread = Environment.CurrentManagedThreadId;
    }

    // Is this script being killed, here? The thread test runs only once the
    // id matches, so outside a kill it costs nothing.
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

public static class ScriptGuardRender
{
    // Written by RenderExecutor's setKillId lambda (v => ScriptGuardRender.KillId = v) — a
    // cross-compilation-unit write the compiler can't see. A host with no render lane
    // (the dedicated server builds only a main Executor) never emits that write, so its
    // build would flag CS0649 here. Suppressed: it is not really "never assigned".
#pragma warning disable CS0649
    internal static volatile int KillId;
#pragma warning restore CS0649

    // See ScriptGuardMain._laneThread.
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
