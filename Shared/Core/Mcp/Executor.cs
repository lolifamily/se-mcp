using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Shared.Plugin;

namespace Shared.Mcp;

// One execute_code request, carried from ExecuteCodeTool to the lane that runs it and back to
// McpServer, which waits on Done. Public because ITool.TryDispatch takes it; plain internal fields,
// since nothing outside the plugin touches them.
public sealed class WorkItem
{
    internal IReadOnlyList<string> Usings;
    internal string ClassBody;
    internal string Code;

    // Set once the request is answered. RunContinuationsAsynchronously keeps McpServer's
    // continuation (rendering the output, writing the response) off the game thread that answers.
    internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Cancelled by notifications/cancelled, or when the client hangs up.
    internal CancellationToken Cancel;

    // The answer: what the script printed, and how it ended.
    internal string Output;
    internal string Error;
    internal bool WasCancelled;

    // Answers the request. Set Output first: McpServer reads it as soon as Done is set.
    internal void Complete(string error = null, bool cancelled = false)
    {
        Error = error;
        WasCancelled = cancelled;
        Done.TrySetResult(true);
    }
}

// A frame lane, "main" or "render". Scripts compile on the thread pool; then the lane's pump, Tick,
// runs once per game frame on the lane's thread and steps each script once: one MoveNext of its
// Run() iterator, so `yield return` gives the frame back to the game and resumes in the next one.
//
// Every script stepped in a frame shares one time budget (FrameWatchdog). The script running when
// it runs out is killed, and the scripts after it end with a retry. A script that ends early is
// answered at once; if it was sitting at a yield, the finally blocks around it still have to run,
// and they do after every live script's step, on the budget left (UnwindAll).
//
// The guard arguments come from the lane's ScriptGuard class (ScriptGuardMain, ScriptGuardRender):
// what Compiler's injected checks read and call, setKillId for the watchdog, and beginStep, called
// before every step. denialMessage is the host's text for a closed deny gate, since Shared holds no SE
// strings. The gate is IPluginConfig.Denied, refreshed once a frame on the main thread and read here
// as a plain bool: atomic, and at worst a frame stale.
public sealed class Executor(
    FieldInfo guardKillId, FieldInfo guardLaneThread, MethodInfo guardStackCheck,
    Action<int> setKillId, Action beginStep,
    string denialMessage,
    int frameTimeoutMs,
    string defaultUsings) : IScriptLane, IDisposable
{
    // Ends a script whose turn came after the frame's budget was spent. Its own code didn't run this
    // frame, so it gets a retry rather than the offender's "split the work" advice.
    private const string SpentBeforeTurnMessage =
        "Script killed: frame budget spent by other scripts before its turn — retry";

    internal volatile bool Initialized;

    bool IScriptLane.Initialized => Initialized;

    private readonly Compiler compiler = new(guardKillId, guardLaneThread, guardStackCheck, defaultUsings, asyncEntry: false);
    private readonly FrameWatchdog watchdog = new(setKillId, frameTimeoutMs);

    // Compiled scripts waiting for the next Tick to start them.
    private readonly ConcurrentQueue<(WorkItem Item, CompilationResult Result)> compiled = new();

    // Running scripts. Tick thread only, like `disposing`.
    private readonly List<ActiveScript> active = [];

    // Scripts that ended at a yield and still owe the finally blocks around it.
    private readonly Queue<ActiveScript> disposing = new();

    // Requests taken and not yet answered, so Dispose can answer them.
    private readonly ConcurrentDictionary<WorkItem, byte> inflight = new();

    private volatile bool disposed;

    // The lane's pump, once Compiler.InitShared returns true. Opens the lane: ExecuteCodeTool turns
    // work away until then.
    public void Initialize()
    {
        if (Initialized) return;
        Initialized = true;
    }

    private sealed class ActiveScript
    {
        public int Id; // CompilationResult.ScriptId: what its injected checks answer to
        public WorkItem Item;
        public CompilationResult Compiled;

        // Made by the first step. Non-null exactly while the script sits at a yield, so one that ends
        // with it set still owes the finally blocks around that yield.
        public IEnumerator<object> Coroutine;

        public Capture Output;
    }

    // Any thread. Compiles on the thread pool; the next Tick starts the script.
    public void Enqueue(WorkItem item)
    {
        if (disposed)
        { CompleteItem(item, error: ScriptRender.Shutdown); return; }

        if (item.Cancel.IsCancellationRequested)
        { CompleteItem(item, cancelled: true); return; }

        if (Common.Config.Denied)
        { CompleteItem(item, error: denialMessage); return; }

        inflight[item] = 0;

        // Dispose may have answered `inflight` between the first check and the add.
        if (disposed)
        {
            CompleteItem(item, error: ScriptRender.Shutdown);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var result = compiler.Compile(item.Usings, item.ClassBody, item.Code, item.Cancel);
                compiled.Enqueue((item, result));
            }
            // Compiler calls Roslyn by reflection, so a cancel arrives wrapped in
            // TargetInvocationException: test the token, not the exception.
            catch (Exception) when (item.Cancel.IsCancellationRequested)
            {
                CompleteItem(item, cancelled: true);
            }
            catch (Exception ex)
            {
                CompleteItem(item, error: ScriptRender.StartFailed(ex));
            }
        });
    }

    // The lane's pump: once per game frame, on the lane's thread. Starts newly compiled scripts,
    // steps every running one, then runs owed cleanup, all within one frame budget.
    //
    // After Dispose, the next Tick is the last chance to run the scripts' finally blocks on the
    // thread they ran on (a render script may hold thread-affine D3D11 state), so Dispose leaves the
    // scripts to it. Their requests are answered already; their cleanup runs on one frame's budget.
    // The render lane goes on getting frames for whatever is left; the main lane gets only the one
    // Tick from Plugin.Dispose, and the rest is dropped.
    public void Tick()
    {
        if (disposed)
        {
            foreach (var s in active)
            {
                s.Output.Take();   // seal it: nobody reads it any more
                if (s.Coroutine != null) disposing.Enqueue(s);
            }
            active.Clear();
        }
        else
        {
            while (compiled.TryDequeue(out var pair))
                Start(pair.Item, pair.Result);
        }

        // Nothing to run: no frame, rather than a timer for nobody 60-240 times a second. Nothing
        // stays armed meanwhile, since the last frame disarmed on its way out, so code a script left
        // behind (a Harmony patch, an event handler) never meets a kill while the lane idles.
        if (active.Count == 0 && disposing.Count == 0)
            return;

        // If a step overruns, no later Tick comes to stop it: the watchdog's timer raises the id of
        // the script on the stack, that script's injected checks throw, and its catch filters refuse
        // while it is being killed, so the unwind reaches StepAll. Begin/End pair in a finally, so no
        // Tick leaves its frame armed.
        watchdog.BeginFrame();
        try
        {
            StepAll();
            UnwindAll();
        }
        finally
        {
            watchdog.EndFrame();
        }
    }

    // Gives each running script its turn. A script ends here when its request is cancelled, the deny
    // gate is closed, the frame's budget is already spent, or its step finishes, throws or overruns.
    private void StepAll()
    {
        // Read once per frame: a gate closing mid-loop stops the rest next frame.
        var denied = Common.Config.Denied;

        for (var i = active.Count - 1; i >= 0; i--)
        {
            var s = active[i];

            if (s.Item.Cancel.IsCancellationRequested)
            {
                Complete(s, cancelled: true);
                active.RemoveAt(i);
                continue;
            }

            if (denied)
            {
                Complete(s, error: denialMessage);
                active.RemoveAt(i);
                continue;
            }

            // Ended rather than deferred: a skipped frame would silently gap a cross-frame series,
            // and a loud failure beats a silent gap.
            if (watchdog.Tripped)
            {
                Complete(s, error: SpentBeforeTurnMessage);
                active.RemoveAt(i);
                continue;
            }

            // A fresh stack baseline for this script, and this thread as the one a kill aims at,
            // both in place before EnterStep can raise a kill.
            beginStep();
            var startedAt = Stopwatch.GetTimestamp();
            var more = false;
            Exception thrown = null;
            watchdog.EnterStep(s.Id);
            try
            {
                // The first step makes the instance, so class_body's initializers run under the
                // watchdog like the rest of the script.
                s.Coroutine ??= s.Compiled.Start(s.Output).GetEnumerator();
                more = s.Coroutine.MoveNext();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                watchdog.ExitStep();
            }

            // Finished or threw: no yield to resume and no finally owed. A throw ran them on its way
            // out, or the kill skipped them; either way they don't run again.
            if (!more) s.Coroutine = null;

            // If the budget ran out during the step, the step ends as a timeout whatever it did:
            // threw, or returned because a blocking call gave its checks nowhere to fire or a
            // `catch when` swallowed the kill. Any other answer would hide that the frame froze.
            var error = watchdog.Tripped ? TimeoutReport(startedAt, thrown)
                : thrown != null ? ScriptRender.Threw(thrown)
                : null;
            if (error == null && more)
                continue;
            Complete(s, error);
            active.RemoveAt(i);
        }

        // A closed gate ends the scripts' code, cleanup included.
        if (denied) disposing.Clear();
    }

    // A compile error answers the request here; otherwise the script joins `active`, not yet
    // instantiated.
    private void Start(WorkItem item, CompilationResult result)
    {
        if (!result.Success)
        {
            CompleteItem(item, error: result.ErrorOutput);
            return;
        }

        active.Add(new ActiveScript
        {
            Id = result.ScriptId,
            Item = item,
            Compiled = result,
            Output = new Capture()
        });
    }

    // Answers the request at once, with the output so far, and seals the output. A script ending at
    // a yield goes to `disposing`, for UnwindAll to run the finally blocks around it.
    private void Complete(ActiveScript s, string error = null, bool cancelled = false)
    {
        s.Item.Output = s.Output.Take();
        CompleteItem(s.Item, error, cancelled);
        if (s.Coroutine != null) disposing.Enqueue(s);
    }

    // Runs the finally blocks of scripts that ended at a yield (IEnumerator.Dispose), after every
    // live script's step, on the budget left. Each runs as a step of its own script, so a finally
    // that loops is killed like any step; once the budget is spent, the rest wait for the next
    // frame. The request is answered already, so a cleanup that throws is only logged.
    private void UnwindAll()
    {
        while (disposing.Count > 0 && !watchdog.Tripped)
        {
            var s = disposing.Dequeue();
            beginStep();
            watchdog.EnterStep(s.Id);
            try
            {
                s.Coroutine.Dispose();
            }
            catch (Exception ex)
            {
                Common.Logger.Warning($"script {s.Id} cleanup threw ({ex.GetType().Name})");
            }
            finally
            {
                watchdog.ExitStep();
            }
        }
    }

    private void CompleteItem(WorkItem item, string error = null, bool cancelled = false)
    {
        item.Complete(error, cancelled);
        inflight.TryRemove(item, out _);
    }

    // Main thread. Answers every open request, without its output: that sits with the scripts, which
    // only the Tick thread touches. The host must then give the lane one more Tick on its own thread
    // (see Tick): the render lane gets it from its next frame, the main lane from Plugin.Dispose,
    // since no pump comes after that. Compiler's shared state is released by Plugin.Dispose too.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Enqueue's second disposed check answers anything added after this snapshot.
        foreach (var item in new List<WorkItem>(inflight.Keys))
            item.Complete(ScriptRender.Shutdown);
        inflight.Clear();
    }

    // The report for a step that overran. Its own time tells the offender from a bystander: the
    // budget is shared, so a small number means other scripts spent it.
    //
    // `thrown` is what the step threw after the kill: normally the guard's exception, with the stack
    // where the step was cut, the only record of it, since .NET can't walk another thread's stack.
    // Anything else got past the catch filters the kill stood down; a genuine fault looks the same
    // from here, so it is shown as-is. Null if the step never reached a check: a blocking call, or a
    // `catch when` that swallowed the kill.
    //
    // No warning about code the script installed: a kill reaches its code only on the lane thread,
    // during this step, so whatever it cut (a patched method the step called, an event it raised) is
    // on the stack shown, and its code anywhere else is never touched.
    private string TimeoutReport(long startedAt, Exception thrown)
    {
        var stepMs = (Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency;
        return $"script interrupted {stepMs}ms into its step (shared frame budget: {frameTimeoutMs}ms):\n"
            + ScriptRender.Stack(thrown ?? new ScriptTimeoutException());
    }
}
