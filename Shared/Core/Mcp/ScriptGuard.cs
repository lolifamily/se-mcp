using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Shared.Mcp;

public class ScriptTimeoutException()
    : Exception("Script killed: shared frame budget ran out mid-step — split work across frames with `yield return null`");

public class ScriptStackException()
    : Exception("Script killed: stack depth budget exhausted (700KB)");

// Two independent guards — one per execution lane (main / render).
// Kept as separate static classes rather than one class with parallel
// fields. Compiler picks which to inject via the pre-resolved Bail/
// StackCheck/KillId handles each class exposes (BailMethod etc.), so
// each lane's KillId is touched only by its own Executor's watchdog
// and read only by its own scripts. StackBase is [ThreadStatic] because
// the writer (the script) and reader (StackCheck) are always the same
// thread; KillId is plain static volatile because one of its writers is
// the watchdog's timer thread, crossing into the executing thread.
//
// KillId is the id of the script to kill on this lane, or 0 for none.
// Every Bail call and catch filter compares it against the id baked into
// its own script at compile time, so a script only ever answers to a kill
// aimed at itself — see FrameWatchdog for why an id and not a flag.

public static class ScriptGuardMain
{
    internal static volatile int KillId;

    [ThreadStatic] internal static long StackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardMain).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardMain).GetMethod(nameof(StackCheck));
    // KillId is internal — GetField defaults to public-only, so NonPublic is required or this is null.
    public static readonly FieldInfo KillIdField = typeof(ScriptGuardMain).GetField(nameof(KillId), BindingFlags.Static | BindingFlags.NonPublic);

    private const long StackBudgetBytes = 700_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail(int scriptId)
    {
        if (KillId == scriptId) throw new ScriptTimeoutException();
    }

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

public static class ScriptGuardRender
{
    // Written by RenderExecutor's setKillId lambda (v => ScriptGuardRender.KillId = v) — a
    // cross-compilation-unit write the compiler can't see. A host with no render lane
    // (the dedicated server builds only a main Executor) never emits that write, so its
    // build would flag CS0649 here. Suppressed: it is not really "never assigned".
#pragma warning disable CS0649
    internal static volatile int KillId;
#pragma warning restore CS0649

    [ThreadStatic] internal static long StackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardRender).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardRender).GetMethod(nameof(StackCheck));
    // KillId is internal — GetField defaults to public-only, so NonPublic is required or this is null.
    public static readonly FieldInfo KillIdField = typeof(ScriptGuardRender).GetField(nameof(KillId), BindingFlags.Static | BindingFlags.NonPublic);

    private const long StackBudgetBytes = 700_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail(int scriptId)
    {
        if (KillId == scriptId) throw new ScriptTimeoutException();
    }

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
