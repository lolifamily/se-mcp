using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace ClientPlugin;

public sealed class WorkItem
{
    public string Code;

    // RunContinuationsAsynchronously is load-bearing: TrySetResult is called from
    // the game's main/render thread (CompleteItem via Tick). Without it the awaiting
    // HandleToolsCall continuation — JSON-escaping the full script output plus the
    // HTTP response write — would run inlined on that game thread.
    public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Cancel;

    public string Output;
    public string Error;
    public bool WasCancelled;
}

// guard{Bail,StackCheck,Dead}: pre-resolved MemberInfos from the ScriptGuard{Main,Render}
// static class whose Bail/Dead/StackCheck get injected into compiled REPL bytecode.
// Resolved once at type init (see ScriptGuardMain.BailMethod etc) instead of reflecting
// per-compile. setDead writes that class's Dead flag from the Task pool deadline timer
// (cross-thread, plain static volatile). resetStackBase writes the [ThreadStatic]
// StackBase field from this Executor's Tick thread (lambda body is `stsfld`, hits the
// calling thread's slot — so the reset lands on the same slot the script's StackCheck
// will later read).
public sealed class Executor(
    MethodInfo guardBail, MethodInfo guardStackCheck, FieldInfo guardDead,
    Action<bool> setDead, Action<long> resetStackBase, int frameTimeoutMs) : IDisposable
{
    private const string DenialMessage = "Multiplayer non-admin: code execution is disabled. You must be Admin or Owner to use SeMcp in multiplayer.";
    private const string ShutdownMessage = "[server shutting down]";

    public volatile bool Initialized;

    private readonly Compiler compiler = new(guardBail, guardStackCheck, guardDead);
    private readonly ConcurrentQueue<(WorkItem Item, CompilationResult Result)> compiled = new();
    private readonly List<ActiveScript> active = [];
    private readonly ConcurrentDictionary<WorkItem, byte> inflight = new();
    private int epoch;
    private volatile bool denied;
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

        if (denied)
        { CompleteItem(item, error: DenialMessage); return; }

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
                var result = compiler.Compile(item.Code);
                compiled.Enqueue((item, result));
            }
            catch (Exception ex)
            {
                CompleteItem(item, error: FormatException(ex));
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
                catch (Exception ex) { MyLog.Default.Warning($"SeMcp: coroutine dispose failed: {ex.Message}"); }
                try { s.Writer?.Dispose(); }
                catch (Exception ex) { MyLog.Default.Warning($"SeMcp: writer dispose failed: {ex.Message}"); }
            }
            active.Clear();
            return;
        }

        while (compiled.TryDequeue(out var pair))
            Start(pair.Item, pair.Result);

        // denied must refresh BEFORE the idle exit below: Enqueue's fast-reject
        // reads it, and a denied-then-idle executor would otherwise never run a
        // script again — nothing reaches `active`, every Tick exits early, and
        // the stale true sticks forever (even back in single player). Two
        // property reads, no allocation; fine to do every frame.
        denied = IsDenied();

        // Idle fast path. Everything below exists to police running scripts;
        // with none, arming the deadline timer would just allocate a closure +
        // ContinueWith + DelayPromise per frame, 60-240 Hz across two lanes,
        // for nobody. A stale timer from the last active frame may still fire
        // during idle and leave Dead set — harmless: the next active frame
        // clears it via setDead(false) before any MoveNext runs.
        if (active.Count == 0)
            return;

        // Deadline timer: each active Tick bumps `epoch` and fires a Task that
        // flips Dead true after frameTimeoutMs — but only if its captured epoch
        // is still current. A later Tick increments epoch and silently invalidates
        // any in-flight timer from a previous frame. If MoveNext stays in a hot
        // loop with no backward branches (e.g. recursive lambda + catch), no
        // later Tick runs and the timer fires, setting Dead. Catches that
        // would otherwise swallow the bail are rejected by the filter handlers
        // we splice in during compilation, so unwind reaches Executor.Tick.
        // setDead writes from a Task pool worker — Dead must be plain volatile,
        // not ThreadStatic, because the writer crosses thread.
        var myEpoch = Interlocked.Increment(ref epoch);
        setDead(false);
        _ = Task.Delay(frameTimeoutMs).ContinueWith(_ =>
        {
            if (Volatile.Read(ref epoch) == myEpoch) setDead(true);
        });

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
                Complete(s, error: DenialMessage);
                active.RemoveAt(i);
                continue;
            }

            // Per-script stack budget: each script gets its own SP baseline.
            // StackBase is [ThreadStatic] on the guard class; this lambda's
            // `stsfld` writes the slot belonging to this Tick's thread — same
            // slot the script's StackCheck will read on the very next line.
            resetStackBase(0);
            try
            {
                if (!s.Coroutine.MoveNext())
                {
                    Complete(s);
                    active.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                Complete(s, error: FormatException(ex));
                active.RemoveAt(i);
            }
        }
    }

    private static bool IsDenied()
    {
        var session = MyAPIGateway.Session;
        if (session == null || session.OnlineMode == MyOnlineModeEnum.OFFLINE)
            return false;
        return session.PromoteLevel < MyPromoteLevel.Admin;
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
            var writer = new StringWriter();

            active.Add(new ActiveScript
            {
                Item = item,
                Coroutine = run(writer).GetEnumerator(),
                Writer = writer
            });
        }
        catch (Exception ex)
        {
            CompleteItem(item, error: FormatException(ex));
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

    private static string FormatException(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: not null } tie)
            ex = tie.InnerException;
        return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
    }
}
