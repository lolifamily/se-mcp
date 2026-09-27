#if NETFRAMEWORK
using System.Threading;

namespace System.Runtime;

// net48 has no ControlledExecution — .NET 7 added it — but it has the abort underneath, as the public
// Thread.Abort. So this is the upstream Run (dotnet/runtime, MIT:
// src/coreclr/System.Private.CoreLib/src/System/Runtime/ControlledExecution.CoreCLR.cs) with its two
// runtime calls swapped for their public forms, and three things net48 lacks worked around:
//   - AbortThread(handle) → Thread.Abort().
//   - ResetAbortThread()  → Thread.ResetAbort(), and only with an abort pending: that throws where
//     the runtime call quietly does nothing.
//   - Unregister()        → Canceler.Disarm(). net48's registration has no Unregister, and Dispose
//     is no stand-in: it waits for a running callback, and that callback — Thread.Abort — waits for
//     this thread to leave the very finally it would be waiting in.
//   - UnsafeRegister      → Register.
//   - The OperationCanceledException doesn't carry the abort's stack: net48 has no
//     ExceptionDispatchInfo.SetRemoteStackTrace.
// Callers write ControlledExecution.Run on both targets; on net48 this is the one they get.
internal static class ControlledExecution
{
    [ThreadStatic]
    private static bool t_executing;

    public static void Run(Action action, CancellationToken cancellationToken)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));
        if (t_executing)
            throw new InvalidOperationException("The thread is already executing the ControlledExecution.Run method.");

        var canceler = new Canceler(Thread.CurrentThread);
        try
        {
            t_executing = true;
            using (cancellationToken.Register(e => ((Canceler)e).Cancel(), canceler))
            {
                try
                {
                    action();
                }
                finally
                {
                    // Too late to disarm: the abort may already be on its way. Keep clearing it
                    // until the canceler is done, after which it can no longer land.
                    if (!canceler.Disarm())
                    {
                        var spinWait = new SpinWait();
                        while (!canceler.IsCancelCompleted)
                        {
                            ResetAbortThread();
                            spinWait.SpinOnce();
                        }
                    }
                }
            }
        }
        catch (ThreadAbortException)
        {
            throw cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new OperationCanceledException();
        }
        finally
        {
            t_executing = false;
            if (cancellationToken.IsCancellationRequested)
                ResetAbortThread();
        }
    }

    private static void ResetAbortThread()
    {
        if ((Thread.CurrentThread.ThreadState & ThreadState.AbortRequested) != 0)
            Thread.ResetAbort();
    }

    private sealed class Canceler(Thread thread)
    {
        private const int Armed = 0, Cancelling = 1, Completed = 2, Disarmed = 3;
        private int state;

        public bool IsCancelCompleted => Volatile.Read(ref state) == Completed;

        // The registration's callback: abort the thread, unless Run has already disarmed it.
        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref state, Cancelling, Armed) != Armed)
                return;
            try
            {
                thread.Abort();
            }
            finally
            {
                Volatile.Write(ref state, Completed);
            }
        }

        // Run, on its way out: true when no abort was started, and from here on none can be.
        public bool Disarm() => Interlocked.CompareExchange(ref state, Disarmed, Armed) == Armed;
    }
}
#endif
