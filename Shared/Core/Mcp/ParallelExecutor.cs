using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Shared.Plugin;

namespace Shared.Mcp;

// The "parallel" lane: each script runs to its end on a thread of its own, alongside the game and
// other scripts, for long computation or blocking I/O that would freeze a frame lane. No frame drives
// it, so there is no budget and no watchdog: `yield return null` resumes at once, and scripts carry
// StackCheck alone. That one stays because a stack overflow takes the whole process, and no kill from
// outside gets there first.
//
// `await` works here and only here. The entry is an async iterator, and the worker runs a ScriptPump
// that the script builders (ScriptBuilders.cs) send every continuation of the script to, whichever
// thread completes what was awaited. So the script always resumes on its worker: the one thread the
// kill reaches, and the one StackCheck's baseline belongs to.
//
// A kill is a real abort (ControlledExecution.Run; on net48 the port in Shared/Core/Tools), so a loop
// with no check in it stops too. Three things kill a script: its request cancelled or its client
// gone, the deny gate closing (EnforceDenyGate), and Dispose. Each kill:
//   - answers the request first, with the output so far, and seals the output: the abort can take
//     arbitrarily long to land, and nobody waits for it;
//   - drops the script's pending continuations (ScriptPump.Kill): one parked at an `await` would
//     otherwise come back to life on the thread pool;
//   - fires the abort from a throwaway thread: Cancel() returns only once the abort lands, which it
//     can't while the script is in a finally, a catch, native code or a wait;
//   - interrupts a script waiting in managed code, at once and every NudgeMs until its thread is gone.
//     On .NET 10 an abort can't wake a wait, and an interrupt is spent by the first catch that
//     swallows it, so one that arrives before the abort is pending is lost.
// None of it stops a script spinning in a finally or a catch, or blocked in native code: its thread
// outlives the request it no longer answers.
public sealed class ParallelExecutor(string denialMessage, string defaultUsings) : IScriptLane, IDisposable
{
    private const int NudgeMs = 250;

    // StackCheck alone: nothing here raises a kill id. ScriptGuardMain's serves every worker, since its
    // baseline is [ThreadStatic] and every script has a thread of its own.
    private readonly Compiler compiler = new(null, null, ScriptGuardMain.StackCheckMethod, defaultUsings, asyncEntry: true);

    // Every script not gone yet: compiling, running, or killed while its thread lives on. Only Sweep
    // removes entries, so a worker never touches this after its script ends: a killed one may have an
    // interrupt pending, and a lock it had to wait on would throw at the top of its thread.
    private readonly ConcurrentDictionary<Script, byte> live = new();

    private Timer sweeper;
    private volatile bool initialized;
    private volatile bool disposed;

    // The main thread's cultures, given to every worker so scripts format numbers and dates the same
    // on every lane. A new thread would take the OS default, and SE1 never sets one.
    private CultureInfo culture;
    private CultureInfo uiCulture;

    public bool Initialized => initialized;

    // Main thread, once Compiler.InitShared returns true. The volatile write publishes the shared
    // references and the cultures to the threads that compile and run scripts.
    public void Initialize()
    {
        if (initialized) return;
        culture = CultureInfo.CurrentCulture;
        uiCulture = CultureInfo.CurrentUICulture;
        sweeper = new Timer(_ => Sweep(), null, NudgeMs, NudgeMs);
        initialized = true;
    }

    // Any thread. Compiles on the thread pool, then gives the script a thread of its own.
    public void Enqueue(WorkItem item)
    {
        if (disposed)
        { item.Complete(ScriptRender.Shutdown); return; }

        if (item.Cancel.IsCancellationRequested)
        { item.Complete(cancelled: true); return; }

        if (Common.Config.Denied)
        { item.Complete(denialMessage); return; }

        var s = new Script(item);
        live[s] = 0;

        // Dispose may have swept `live` between the first check and the add.
        if (disposed)
        { Finish(s, ScriptRender.Shutdown); return; }

        // Runs right here if the request was cancelled in the meantime.
        item.Cancel.Register(() => Kill(s, null, cancelled: true));
        Task.Run(() => Launch(s));
    }

    // Main thread, every frame, right after the deny gate is refreshed. A script with no check in it
    // never looks at the gate, so a closed gate is pushed onto it from here.
    public void EnforceDenyGate()
    {
        if (!live.IsEmpty && Common.Config.Denied)
            KillAll(denialMessage);
    }

    // Answers every open request and aborts every running script. Workers are background threads:
    // whatever an abort can't reach dies with the process.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        KillAll(ScriptRender.Shutdown);
        sweeper?.Dispose();
    }

    private void KillAll(string reason)
    {
        foreach (var pair in live)
            Kill(pair.Key, reason);
    }

    // Thread pool: compile, then start the script's thread.
    private void Launch(Script s)
    {
        try
        {
            // The abort token, not the request's: Kill fires it only after answering, so a cancelled
            // compile never answers with its own exception, and the deny gate and Dispose fire it too.
            var result = compiler.Compile(s.Item.Usings, s.Item.ClassBody, s.Item.Code, s.Abort.Token);
            if (!result.Success)
            {
                Finish(s, result.ErrorOutput);
                return;
            }

            s.Compiled = result;
            var worker = new Thread(() => Work(s))
            {
                IsBackground = true,
                Name = "SeMcp parallel #" + result.ScriptId,
                CurrentCulture = culture,
                CurrentUICulture = uiCulture
            };
            if (s.Finished) return;   // killed while it compiled
            s.Worker = worker;        // before Start: Nudge must see every thread that can run a script
            worker.Start();
        }
        catch (Exception ex)
        {
            Finish(s, ScriptRender.StartFailed(ex));
        }
    }

    // The worker thread's whole life.
    private static void Work(Script s)
    {
        string error = null;
        try
        {
            if (s.Finished) return;   // killed before its thread got going
#pragma warning disable SYSLIB0046 // the abort is the point: nothing else stops a loop with no check in it
            ControlledExecution.Run(() => Drive(s), s.Abort.Token);
#pragma warning restore SYSLIB0046
        }
        catch (Exception ex)
        {
            // After a kill this is the abort (OperationCanceledException) or a nudge
            // (ThreadInterruptedException), and Finish below answers nobody.
            error = ScriptRender.Threw(ex);
        }
        Finish(s, error);
    }

    // The script, inside ControlledExecution.Run. Making the instance runs class_body's initializers,
    // the script's own code, so it happens in here too: within the abort's reach, and with the pump
    // already in place, so async work they start comes back here as well.
    private static void Drive(Script s)
    {
        Task run = null;
        s.InScript = true;
        try
        {
            ScriptPump.Current = s.Pump;

            // The call runs the script up to its first await; the pump runs the rest on this thread.
            run = s.Compiled.StartAsync(s.Output)();
            s.Pump.RunUntil(run);
            run.GetAwaiter().GetResult();   // the script's own exception, as it threw it
        }
        finally
        {
            // Cleared before Run's own cleanup, which no nudge may reach: it waits for the canceller
            // with a SpinWait that sleeps, and an interrupt there would cut that wait short.
            s.InScript = false;

            // A script that finished leaves its loose ends running; one that didn't (killed, or never
            // started) leaves none. A no-op when a kill closed the pump already.
            if (run is { IsCompleted: true }) s.Pump.End();
            else s.Pump.Kill();
        }
    }

    // Answers the request now, then aborts the script.
    private static void Kill(Script s, string error, bool cancelled = false)
    {
        if (!Finish(s, error, cancelled)) return;
        s.Killed = true;
        s.Pump.Kill();
        new Thread(() =>
        {
            // Returns once the abort has landed, or at once if no worker has registered for it yet:
            // one that starts later finds the script finished and never runs it.
            try { s.Abort.Cancel(); }
            catch (Exception ex) { Common.Logger.Warning($"parallel abort failed: {ex.Message}"); }
        }) { IsBackground = true, Name = "SeMcp parallel abort" }.Start();
        // Wakes most waiting scripts; Sweep retries for those that swallow it.
        Nudge(s);
    }

    // Answers the request, once: whichever ending gets here first.
    private static bool Finish(Script s, string error, bool cancelled = false)
    {
        if (!s.TryFinish()) return false;
        s.Item.Output = s.Output.Take();
        s.Item.Complete(error, cancelled);
        return true;
    }

    // Timer thread, every NudgeMs: nudges killed scripts again, and forgets those whose thread is gone.
    // Nothing may escape: a timer callback that throws ends the process.
    private void Sweep()
    {
        if (live.IsEmpty) return;
        try
        {
            foreach (var pair in live)
            {
                var s = pair.Key;
                if (s.Worker is { IsAlive: true })
                {
                    if (s.Killed) Nudge(s);
                }
                else if (s.Finished)
                {
                    live.TryRemove(s, out _);
                }
            }
        }
        catch (Exception ex)
        {
            Common.Logger.Warning($"parallel sweep failed: {ex.Message}");
        }
    }

    // Interrupts the worker if it is in a managed wait inside the script's own code: the one place an
    // interrupt is needed, and the one place it can't cut Run's own cleanup short.
    private static void Nudge(Script s)
    {
        var worker = s.Worker;
        if (worker != null && s.InScript && (worker.ThreadState & ThreadState.WaitSleepJoin) != 0)
            worker.Interrupt();
    }

    private sealed class Script(WorkItem item)
    {
        public readonly WorkItem Item = item;

        // The script's Console. A kill takes it before firing the abort, so an abort landing mid-write
        // can't tear text anyone reads.
        public readonly Capture Output = new();

        // ControlledExecution.Run's token: cancelling it is the abort.
        public readonly CancellationTokenSource Abort = new();

        // Where the script's continuations queue for its worker.
        public readonly ScriptPump Pump = new();

        public CompilationResult Compiled;

        // Published before Start, so Nudge sees every thread that can run the script.
        public volatile Thread Worker;

        // The script's own code is on the worker's stack: the only time a nudge may land.
        public volatile bool InScript;

        public volatile bool Killed;

        private int finished;

        public bool Finished => Volatile.Read(ref finished) != 0;

        // True for exactly one ending, the one that answers: running out, throwing, or a kill.
        public bool TryFinish() => Interlocked.Exchange(ref finished, 1) == 0;
    }
}
