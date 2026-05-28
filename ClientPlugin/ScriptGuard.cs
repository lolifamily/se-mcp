using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ClientPlugin;

public static class ScriptGuard
{
    public static long Deadline;
    private static int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Tick()
    {
        if ((++_count & 0x3FF) != 0) return;
        if (Stopwatch.GetTimestamp() > Deadline)
            throw new TimeoutException("Script killed: 1s/frame budget exhausted (shared across all in-flight scripts)");
    }
}
