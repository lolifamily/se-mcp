using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Shared.Plugin;

namespace Shared.Mcp;

// The off-frame lane: each script runs to its end on a thread of its own, concurrently with the
// game and with other scripts — for long computation or blocking I/O that would freeze a frame
// lane. No game frame drives it, so there is no frame budget and no watchdog: `yield return null`
// resumes at once, and scripts compile with StackCheck alone (Compiler.InjectGuards). That one
// stays: a stack overflow takes the whole process, and no kill from outside gets there first.
//
// `await` works here, and only here: the entry is an async iterator (Compiler's asyncEntry), and the
// worker runs it with a ScriptPump — the script's own continuation queue — that the script builders
// send every continuation of the script to (ScriptBuilders.cs). So whatever a script awaits, and
// whichever thread completes it, the script resumes on its worker: the one thread the kill below
// reaches and StackCheck's baseline belongs to.
//
// A kill is a real abort — ControlledExecution.Run (net48: the port in Shared/Core/Tools) — so a
// loop with no check point in it stops too. Three things kill a script: its request cancelled (or
// its client gone), the deny gate closing (EnforceDenyGate), and Dispose. Each goes the same way:
//   - The request is answered first, with the output so far, and the output sealed: the abort can
//     take arbitrarily long to land, and nobody waits for it.
//   - Its pump is killed (ScriptPump.Kill): the script's continuations still to come are dropped —
//     one parked at an `await` would otherwise come back to life on the thread pool.
//   - The abort is fired from a throwaway thread: Cancel() returns only once it has landed, which
//     it can't while the script is in a finally, a catch, native code or a wait.
//   - A killed script sitting in a managed wait is interrupted at once, and again every NudgeMs
//     until its thread is gone. On .NET 10 an abort can't wake a wait, and an interrupt is spent
//     by the first catch that swallows it — one arriving before the abort is pending is lost.
// None of it stops a script spinning in a finally or a catch, or blocked in native code: its
// thread then outlives the request it no longer answers.
public sealed class ParallelExecutor(string denialMessage, string defaultUsings) : IScriptLane, IDisposable
{
    private const int NudgeMs = 250;

    // StackCheck alone: nothing here would ever raise a kill id. Borrowed from the main lane's
    // guard — _stackBase is [ThreadStatic], and every script gets a thread of its own.
    private readonly Compiler compiler = new(null, ScriptGuardMain.StackCheckMethod, null, defaultUsings, asyncEntry: true);

    // Every script not yet gone: compiling, running, or killed while its thread lives on. Only
    // Sweep removes entries, so a worker never touches this after its script: a killed one may
    // have an interrupt pending, and any lock it had to wait on would throw at the top of its
    // thread.
    private readonly ConcurrentDictionary<Script, byte> live = new();

    private Timer sweeper;
    private volatile bool initialized;
    private volatile bool disposed;

    // The main thread's cultures, given to every worker so a script formats numbers and dates the
    // same on every lane: a new thread would take the OS default otherwise, and SE1 never sets one.
    private CultureInfo culture;
    private CultureInfo uiCulture;

    public bool Initialized => initialized;

    // Main thread, after Compiler.InitShared: the volatile write below is what publishes the shared
    // references — and the cultures — to the threads that compile and run scripts.
    public void Initialize()
    {
        if (initialized) return;
        culture = CultureInfo.CurrentCulture;
        uiCulture = CultureInfo.CurrentUICulture;
        sweeper = new Timer(_ => Sweep(), null, NudgeMs, NudgeMs);
        initialized = true;
    }

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

    // Main thread, every frame, right after the deny gate is refreshed there: a script with no
    // check in it never looks at the gate, so a closed gate is pushed onto it from here.
    public void EnforceDenyGate()
    {
        if (!live.IsEmpty && Common.Config.Denied)
            KillAll(denialMessage);
    }

    // Answers every request still open and aborts every script still running. Workers are
    // background threads: whatever an abort can't reach dies with the process.
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

    // Thread pool: compile, then hand the script to a thread of its own.
    private void Launch(Script s)
    {
        try
        {
            var result = compiler.Compile(s.Item.Usings, s.Item.ClassBody, s.Item.Code);
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
            ControlledExecution.Run(() => error = Drive(s), s.Abort.Token);
#pragma warning restore SYSLIB0046
        }
        catch (Exception ex)
        {
            // After a kill this is its abort (OperationCanceledException) or a nudge
            // (ThreadInterruptedException), and Finish below answers nobody.
            error = ScriptRender.Threw(ex);
        }
        Finish(s, error);
    }

    // The script, inside ControlledExecution.Run. Returns the report on a script that could not
    // start, or null once it ran to its end. Instantiating runs class_body's initializers — the
    // script's own code — so that happens in here too, where an abort can reach it, and with the
    // pump in place already, so async work they start comes back here as well.
    private static string Drive(Script s)
    {
        Task run = null;
        s.InScript = true;
        try
        {
            ScriptPump.Current = s.Pump;
            Func<Task> start;
            try
            {
                start = s.Compiled.StartAsync(s.Output);
            }
            catch (Exception ex)
            {
                return ScriptRender.StartFailed(ex);
            }

            // The call runs the script up to its first await; the pump runs the rest of it, on this
            // thread. No frame to wait for either: `yield return null` resumes at once.
            run = start();
            s.Pump.RunUntil(run);
            run.GetAwaiter().GetResult();   // the script's own exception, as it threw it
            return null;
        }
        finally
        {
            // Before Run's own cleanup, which no nudge may reach: it waits for the canceller with
            // SpinWait, which sleeps, and an interrupt landing there would cut that wait short.
            s.InScript = false;

            // A script that finished leaves its loose ends running; one that didn't — killed, or
            // never started — leaves none of its own. A no-op when a kill closed the pump already.
            if (run is { IsCompleted: true }) s.Pump.End();
            else s.Pump.Kill();
        }
    }

    // Answer the request now, then abort the script.
    private static void Kill(Script s, string error, bool cancelled = false)
    {
        if (!Finish(s, error, cancelled)) return;
        s.Killed = true;
        s.Pump.Kill();
        new Thread(() =>
        {
            // Returns once the abort has landed — or at once while no worker has registered for
            // it: one that starts later finds the script finished and never runs it.
            try { s.Abort.Cancel(); }
            catch (Exception ex) { Common.Logger.Warning($"parallel abort failed: {ex.Message}"); }
        }) { IsBackground = true, Name = "SeMcp parallel abort" }.Start();
        // Most scripts in a wait go on this one; Sweep retries for those that swallow it.
        Nudge(s);
    }

    // Answers the request — once, by whichever ending gets here first.
    private static bool Finish(Script s, string error, bool cancelled = false)
    {
        if (!s.TryFinish()) return false;
        s.Item.Output = s.Output.Take();
        s.Item.Complete(error, cancelled);
        return true;
    }

    // Timer thread, every NudgeMs: nudge killed scripts again, and forget the ones whose thread is
    // gone. Nothing may escape: a timer callback that throws ends the process.
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

    // Interrupt the worker if it sits in a managed wait inside the script itself: the one place an
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

        // The script's Console. A kill takes it before it fires the abort (Finish), so an abort
        // landing mid-write can't tear text anyone reads.
        public readonly Capture Output = new();

        // Handed to ControlledExecution.Run: cancelling it is the abort.
        public readonly CancellationTokenSource Abort = new();

        // Where the script's continuations queue for its worker.
        public readonly ScriptPump Pump = new();

        public CompilationResult Compiled;

        // Published before Start, so Nudge sees every thread that can run a script.
        public volatile Thread Worker;

        // The script's own code is on the worker's stack — the only time a nudge may land.
        public volatile bool InScript;

        public volatile bool Killed;

        private int finished;

        public bool Finished => Volatile.Read(ref finished) != 0;

        // Exactly one ending answers the request: running out, throwing, or a kill.
        public bool TryFinish() => Interlocked.Exchange(ref finished, 1) == 0;
    }
}
