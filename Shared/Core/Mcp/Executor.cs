using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Shared.Plugin;

namespace Shared.Mcp;

// Internal data carrier threaded through ITool.TryDispatch. The class stays public
// because ITool.TryDispatch is a public interface method and can't take an internal
// parameter (CS0051) — but the fields are internal: they're touched only within the
// plugin assembly (Shared compiles into each plugin dll alongside the tools that use
// them), never by user scripts (which only get a TextWriter) or external callers. So
// there's no CA1051 "visible instance field" exposure, and no reason to wrap a pure
// zero-logic DTO in properties.
public sealed class WorkItem
{
    internal IReadOnlyList<string> Usings;
    internal string ClassBody;
    internal string Code;

    // RunContinuationsAsynchronously is load-bearing: TrySetResult is called from
    // the game's main/render thread (CompleteItem via Tick). Without it the awaiting
    // HandleToolsCall continuation — JSON-escaping the full script output plus the
    // HTTP response write — would run inlined on that game thread.
    internal readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Cancel;

    internal string Output;
    internal string Error;
    internal bool WasCancelled;
}

// guard{Bail,StackCheck,KillId}: pre-resolved MemberInfos from the ScriptGuard{Main,Render}
// static class whose Bail/KillId/StackCheck get injected into compiled REPL bytecode.
// Resolved once at type init (see ScriptGuardMain.BailMethod etc) instead of reflecting
// per-compile. setKillId writes that class's KillId for this lane's FrameWatchdog, from
// the Tick thread and from the watchdog's timer thread (cross-thread, plain static
// volatile). resetStackBase writes the [ThreadStatic] StackBase field from this
// Executor's Tick thread (lambda body is `stsfld`, hits the calling thread's slot — so
// the reset lands on the same slot the script's StackCheck will later read).
//
// denialMessage: the user-facing reject text (Shared holds no SE business strings).
// The denial gate itself lives on IPluginConfig.Denied — the owning Plugin.Update
// refreshes it on the main thread once per frame (client checks SE Admin/Owner
// level; server never writes, stays false). Executor reads Common.Config.Denied
// directly; bool reads are atomic and one frame of staleness is fine.
public sealed class Executor(
    MethodInfo guardBail, MethodInfo guardStackCheck, FieldInfo guardKillId,
    Action<int> setKillId, Action<long> resetStackBase,
    string denialMessage,
    int frameTimeoutMs,
    string defaultUsings) : IDisposable
{
    private const string ShutdownMessage = "[server shutting down]";

    // Ends a script the pump reached after the frame's budget was already spent. Its own
    // code never ran this frame, so it must not read the offender's "split the work" advice.
    private const string SpentBeforeTurnMessage =
        "Script killed: frame budget spent by other scripts before its turn — retry";

    // First line of the report on a script that ended in an exception. It follows the script's own
    // output (ScriptRender.Combine), so it has to say where that output stops and what went wrong.
    // Compile errors need none: each diagnostic names its field.
    private const string StartFailedLabel = "script failed to start:\n";
    private const string ThrewLabel = "script threw:\n";

    internal volatile bool Initialized;

    private readonly Compiler compiler = new(guardBail, guardStackCheck, guardKillId, defaultUsings);
    private readonly FrameWatchdog watchdog = new(setKillId, frameTimeoutMs);
    private readonly ConcurrentQueue<(WorkItem Item, CompilationResult Result)> compiled = new();
    private readonly List<ActiveScript> active = [];
    private readonly ConcurrentDictionary<WorkItem, byte> inflight = new();
    private volatile bool disposed;

    public void Initialize()
    {
        if (Initialized) return;
        // Compiler.InitShared is process-wide and called by Plugin.Update before
        // either Executor.Initialize. Nothing per-Executor needs to happen here
        // beyond flipping the gate the McpServer reads.
        Initialized = true;
    }

    private sealed class ActiveScript
    {
        public int Id; // CompilationResult.ScriptId — what its injected checks answer to
        public WorkItem Item;
        public IEnumerator<object> Coroutine;
        public StringWriter Writer;
    }

    public void Enqueue(WorkItem item)
    {
        if (disposed)
        { CompleteItem(item, error: ShutdownMessage); return; }

        if (item.Cancel.IsCancellationRequested)
        { CompleteItem(item, cancelled: true); return; }

        if (Common.Config.Denied)
        { CompleteItem(item, error: denialMessage); return; }

        inflight[item] = 0;

        // Double-check disposed after adding to inflight. If Dispose ran in between
        // the first check and the add, its drain loop may have missed this item;
        // self-correct here so no WorkItem leaks past Dispose.
        if (disposed)
        {
            CompleteItem(item, error: ShutdownMessage);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var result = compiler.Compile(item.Usings, item.ClassBody, item.Code);
                compiled.Enqueue((item, result));
            }
            catch (Exception ex)
            {
                CompleteItem(item, error: StartFailedLabel + ScriptRender.Stack(ex));
            }
        });
    }

    public void Tick()
    {
        // Disposed: drain `active` here, because IEnumerator.Dispose() runs the
        // script's finally blocks — and those blocks MUST execute on the same
        // thread the script ran on. Dispose() is called from the main thread for
        // both executors; if it touched the render Executor's `active`, every
        // render-thread script's finally would suddenly run on the main thread,
        // breaking user code that observed Thread.CurrentThread or held
        // thread-affine D3D11 resources. So Dispose() never touches `active` —
        // this Tick (= the script's owning thread) drains it. Tick is the sole
        // writer of `active`, so no lock. Repeat disposed-path Ticks no-op.
        if (disposed)
        {
            foreach (var s in active)
            {
                try { s.Coroutine?.Dispose(); }
                catch (Exception ex) { Common.Logger.Warning($"coroutine dispose failed: {ex.Message}"); }
                try { s.Writer?.Dispose(); }
                catch (Exception ex) { Common.Logger.Warning($"writer dispose failed: {ex.Message}"); }
            }
            active.Clear();
            return;
        }

        while (compiled.TryDequeue(out var pair))
            Start(pair.Item, pair.Result);

        // Idle fast path. Everything below exists to police running scripts;
        // with none, arming the watchdog would just allocate a timer per frame,
        // 60-240 Hz across two lanes, for nobody. Nothing is left standing
        // meanwhile: the last active frame disarmed on its way out, so code a
        // finished script left behind (Harmony patches, event handlers) never
        // meets a kill while the lane idles.
        if (active.Count == 0)
            return;

        // One budget per frame, shared by every script StepAll steps: they run
        // serially on this thread, so their combined time is what freezes it.
        // If a step overruns, no later Tick comes to stop it — the watchdog's
        // timer thread raises the id of the script on the stack instead; that
        // script's injected checks throw on it, and its catch filters refuse
        // while it is being killed, so the unwind reaches StepAll. Begin/End
        // pair in a finally, so no Tick leaves its frame armed.
        watchdog.BeginFrame();
        try
        {
            StepAll();
        }
        finally
        {
            watchdog.EndFrame();
        }
    }

    private void StepAll()
    {
        // Local snapshot for the for-loop: one IPluginConfig dispatch instead of N.
        // Stale across the loop is fine — at worst one frame of running scripts gets
        // through before next Tick aborts them.
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

            // Budget already spent, so this script gets no step this frame. End
            // it rather than defer it: a skipped frame would silently gap a
            // cross-frame series, and a loud failure beats a silent gap.
            if (watchdog.Tripped)
            {
                Complete(s, error: SpentBeforeTurnMessage);
                active.RemoveAt(i);
                continue;
            }

            // Per-script stack budget: each script gets its own SP baseline.
            // StackBase is [ThreadStatic] on the guard class; this lambda's
            // `stsfld` writes the slot belonging to this Tick's thread — same
            // slot the script's StackCheck will read on the very next line.
            resetStackBase(0);
            var startedAt = Stopwatch.GetTimestamp();
            var more = false;
            Exception thrown = null;
            watchdog.EnterStep(s.Id);
            try
            {
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

            // Tripped now but not at the check above: the budget ran out during
            // this step, which ends as a timeout whatever it did — threw, or
            // returned because a blocking call gave its checks nowhere to fire or
            // a `catch when` swallowed the kill. A normal result would be the one
            // answer that leaves the caller no way to learn the frame stood frozen.
            var error = watchdog.Tripped ? TimeoutReport(startedAt, thrown)
                : thrown != null ? ThrewLabel + ScriptRender.Stack(thrown)
                : null;
            if (error == null && more)
                continue;
            Complete(s, error);
            active.RemoveAt(i);
        }
    }

    private void Start(WorkItem item, CompilationResult result)
    {
        if (!result.Success)
        {
            CompleteItem(item, error: result.ErrorOutput);
            return;
        }

        try
        {
            var type = result.Assembly?.GetType("__REPL__");
            var method = type?.GetMethod("Run");
            if (method == null)
            {
                CompleteItem(item, error: "Failed to find __REPL__.Run in compiled assembly");
                return;
            }

            var instance = Activator.CreateInstance(type);
            var run = (Func<TextWriter, IEnumerable<object>>)Delegate.CreateDelegate(
                typeof(Func<TextWriter, IEnumerable<object>>), instance, method);
            // \n, not the platform's \r\n: the text goes to a model, and ScriptRender joins on \n.
            var writer = new StringWriter { NewLine = "\n" };

            active.Add(new ActiveScript
            {
                Id = result.ScriptId,
                Item = item,
                Coroutine = run(writer).GetEnumerator(),
                Writer = writer
            });
        }
        catch (Exception ex)
        {
            CompleteItem(item, error: StartFailedLabel + ScriptRender.Stack(ex));
        }
    }

    private void Complete(ActiveScript s, string error = null, bool cancelled = false)
    {
        s.Coroutine.Dispose();
        s.Item.Output = s.Writer.ToString();
        CompleteItem(s.Item, error, cancelled);
    }

    private void CompleteItem(WorkItem item, string error = null, bool cancelled = false)
    {
        item.Error = error;
        item.WasCancelled = cancelled;
        item.Done.TrySetResult(true);
        inflight.TryRemove(item, out _);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Fulfill all inflight promises so HandleToolsCall wakes up and writes responses.
        // ConcurrentDictionary snapshot is safe; TryRemove from a concurrent Complete()
        // on the owner thread is idempotent against this Clear().
        // Items that race in via Enqueue after this point are handled by the double-check
        // in Enqueue (it sees disposed=true after adding to inflight and self-completes).
        foreach (var item in new List<WorkItem>(inflight.Keys))
        {
            item.Error = ShutdownMessage;
            item.Done.TrySetResult(true);
        }
        inflight.Clear();

        // `active` is owned by the Tick thread. Its drain happens on the next Tick
        // (disposed branch). Caller must arrange one final Tick on the owner thread
        // after Dispose: render Executor relies on the natural next-frame hook;
        // main Executor must be ticked once from Plugin.Dispose since SE stops
        // calling Update after dispose. Partial-output capture before promise
        // fulfillment is dropped — it required reading Writer concurrently with
        // a possibly-still-running script.

        // Compiler's shared state (references, resolve handler) is process-wide;
        // it's released by Plugin.Dispose once both executors are torn down.
    }

    // The step's own duration is what tells a victim from the offender: the budget is shared by
    // every script stepped in the frame, so a small value means another script spent it and this
    // one merely happened to be on the stack when it ran out.
    //
    // `thrown` came after the kill. The guard's ScriptTimeoutException carries the stack of where
    // the step was cut; anything else got past the catch filters the kill stood down — a genuine
    // fault looks the same from here, so it is shown as-is. Null when the step reached no check: a
    // blocking call, or a `catch when` that swallowed the kill. .NET has no safe way to walk another
    // running thread's stack, so the watchdog can't photograph the lane at expiry — a thrown stack
    // is the only record of where the step was.
    private string TimeoutReport(long startedAt, Exception thrown)
    {
        var stepMs = (Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency;
        return $"script interrupted {stepMs}ms into this step (shared frame budget: {frameTimeoutMs}ms); "
            + "handlers it installed (Harmony patches, event handlers, spawned threads) may also have been hit:\n"
            + ScriptRender.Stack(thrown ?? new ScriptTimeoutException());
    }
}
