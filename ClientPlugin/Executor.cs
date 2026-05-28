using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    public readonly TaskCompletionSource<bool> Done = new();
    public CancellationToken Cancel;

    public string Output;
    public string Error;
    public bool WasCancelled;
}

public sealed class Executor : IDisposable
{
    private const int FrameTimeoutMs = 1000;

    private const string DenialMessage = "Multiplayer non-admin: code execution is disabled. You must be Admin or Owner to use SeMcp in multiplayer.";
    private const string ShutdownMessage = "[server shutting down]";

    public volatile bool Initialized;

    private readonly Compiler compiler = new();
    private readonly ConcurrentQueue<(WorkItem Item, CompilationResult Result)> compiled = new();
    private readonly List<ActiveScript> active = [];
    private readonly ConcurrentDictionary<WorkItem, byte> inflight = new();
    private volatile bool denied;
    private volatile bool disposed;

    private static readonly Assembly PluginAssembly = typeof(ScriptGuard).Assembly;

    public void Initialize()
    {
        compiler.CollectReferences();
        AppDomain.CurrentDomain.AssemblyResolve += ResolvePluginAssembly;
        Initialized = true;
    }

    private static Assembly ResolvePluginAssembly(object sender, ResolveEventArgs args)
    {
        return new AssemblyName(args.Name).Name == PluginAssembly.GetName().Name
            ? PluginAssembly
            : null;
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
        while (compiled.TryDequeue(out var pair))
            Start(pair.Item, pair.Result);

        denied = IsDenied();
        ScriptGuard.Deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * FrameTimeoutMs / 1000;

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

        // Capture partial output from running scripts before completing their promises,
        // so HandleToolsCall sees the output that was produced before shutdown.
        foreach (var s in active)
            s.Item.Output = s.Writer?.ToString() ?? "";

        // Step 1: fulfill all inflight promises so HandleToolsCall wakes up and starts writing responses.
        // Snapshot keys to avoid mutating the collection while iterating.
        // Items that race in via Enqueue after this point are handled by the double-check
        // in Enqueue (it sees disposed=true after adding to inflight and self-completes).
        foreach (var item in new List<WorkItem>(inflight.Keys))
        {
            item.Error = ShutdownMessage;
            item.Done.TrySetResult(true);
        }
        inflight.Clear();

        // Step 2: run cleanup of active scripts (user-script using/finally blocks).
        // The time this takes incidentally gives the HTTP responses a window to flush out.
        foreach (var s in active)
        {
            try { s.Coroutine?.Dispose(); }
            catch (Exception ex) { MyLog.Default.Warning($"SeMcp: coroutine dispose failed: {ex.Message}"); }
            try { s.Writer?.Dispose(); }
            catch (Exception ex) { MyLog.Default.Warning($"SeMcp: writer dispose failed: {ex.Message}"); }
        }
        active.Clear();

        AppDomain.CurrentDomain.AssemblyResolve -= ResolvePluginAssembly;
    }

    private static string FormatException(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: not null } tie)
            ex = tie.InnerException;
        return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
    }
}
