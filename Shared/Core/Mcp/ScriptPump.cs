using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shared.Plugin;

namespace Shared.Mcp;

// A parallel script's continuation queue, run by its worker thread. The script builders (ScriptBuilders.cs)
// send every continuation of the script's async methods here, whichever thread completes what was awaited,
// so the script keeps coming back to its worker: the one thread its kill (ControlledExecution's abort)
// reaches and its StackCheck baseline belongs to.
//
// Only the script's own continuations come here. No SynchronizationContext is involved, so the BCL and
// engine async code a script calls resumes wherever it always would. Once the script is over:
//   - it finished, normally or by throwing (End): what is still to come — work it started and never
//     awaited — runs on the thread pool, as it would have with no pump at all;
//   - it didn't (Kill): what is still to come is dropped, or a script stopped at an `await` would come back
//     to life on the thread pool, out of the kill's reach for good.
internal sealed class ScriptPump
{
    // The pump of the script running on this thread: set by its worker, null on every other thread.
    [ThreadStatic] internal static ScriptPump Current;

    private const int Running = 0, Ended = 1, Killed = 2;

    private readonly object gate = new();
    private readonly Queue<Action> queue = new();

    // Written under gate; Close reads it once without. Leaves Running once, to Ended or Killed, and never
    // changes again.
    private volatile int phase;

    // A continuation's way back: what the awaiter calls once the awaited thing completes, on whatever thread
    // that is. Always queued, never run in place, so the script resumes at the top of RunUntil's loop and
    // nowhere inside other code.
    internal Action Resume(Action continuation) => () => Enqueue(continuation);

    // Worker thread: runs what arrives until `run`, the script, completes — and returns then, with work
    // still queued if there is any: work the script started and didn't await, which must not keep its
    // answer waiting. End sends it to the thread pool.
    internal void RunUntil(Task run)
    {
        // A completion on another thread still has to wake the wait below.
        run.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(Wake);
        while (true)
        {
            Action next;
            lock (gate)
            {
                while (!run.IsCompleted && queue.Count == 0)
                    Monitor.Wait(gate);
                if (run.IsCompleted) return;
                next = queue.Dequeue();
            }
            next();
        }
    }

    // The script finished. A no-op when a kill got here first.
    internal void End() => Close(Ended);

    // The script didn't finish: killed, or it never got going. Any thread.
    internal void Kill() => Close(Killed);

    private void Wake()
    {
        lock (gate) Monitor.Pulse(gate);
    }

    private void Enqueue(Action continuation)
    {
        int closed;
        lock (gate)
        {
            if (phase == Running)
            {
                queue.Enqueue(continuation);
                Monitor.Pulse(gate);
                return;
            }
            closed = phase;
        }
        Dispatch(continuation, closed);
    }

    private void Close(int to)
    {
        // A killed script's worker comes through here on its way out, perhaps with an interrupt pending: it
        // must not wait on a lock for what is settled already.
        if (phase != Running) return;

        Action[] left;
        lock (gate)
        {
            if (phase != Running) return;
            phase = to;
            left = [.. queue];
            queue.Clear();
        }
        foreach (var continuation in left)
            Dispatch(continuation, to);
    }

    private static void Dispatch(Action continuation, int closed)
    {
        if (closed == Killed) return;
        ThreadPool.QueueUserWorkItem(RunDetached, continuation);
    }

    // Nothing may escape: an exception on a thread-pool thread ends the process. What can throw here is an
    // async void method's exception (ScriptVoidBuilder), arriving after its script ended; it goes to the log.
    private static void RunDetached(object continuation)
    {
        try
        {
            ((Action)continuation)();
        }
        catch (Exception ex)
        {
            Common.Logger.Warning($"parallel script work threw after its script ended: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
