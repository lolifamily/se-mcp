using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClientPlugin;

public class ScriptTimeoutException()
    : Exception("Script killed: 1s/frame budget exhausted");

public class ScriptStackException()
    : Exception("Script killed: stack depth budget exhausted (700KB)");

// Two independent guards — one per execution lane (main / render).
// Kept as separate static classes rather than one class with parallel
// fields. Compiler picks which to inject via the pre-resolved Bail/
// StackCheck/Dead handles each class exposes (BailMethod etc), so
// each lane's Dead flag is touched only by its own Executor's watchdog
// and read only by its own scripts. StackBase is [ThreadStatic] because
// the writer (the script) and reader (StackCheck) are always the same
// thread; Dead is plain static volatile because the writer is a Task
// pool worker (the deadline timer) crossing into the executing thread.

public static class ScriptGuardMain
{
    public static volatile bool Dead;

    [ThreadStatic] public static long StackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardMain).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardMain).GetMethod(nameof(StackCheck));
    public static readonly FieldInfo DeadField = typeof(ScriptGuardMain).GetField(nameof(Dead));

    private const long StackBudgetBytes = 700_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail()
    {
        if (Dead) throw new ScriptTimeoutException();
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
    public static volatile bool Dead;

    [ThreadStatic] public static long StackBase;

    public static readonly MethodInfo BailMethod = typeof(ScriptGuardRender).GetMethod(nameof(Bail));
    public static readonly MethodInfo StackCheckMethod = typeof(ScriptGuardRender).GetMethod(nameof(StackCheck));
    public static readonly FieldInfo DeadField = typeof(ScriptGuardRender).GetField(nameof(Dead));

    private const long StackBudgetBytes = 700_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bail()
    {
        if (Dead) throw new ScriptTimeoutException();
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
