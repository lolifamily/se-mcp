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

public sealed class Executor
{
    private readonly Compiler compiler = new();
    private readonly ConcurrentQueue<WorkItem> queue = new();
    private readonly List<ActiveScript> active = [];

    private sealed class ActiveScript
    {
        public WorkItem Item;
        public IEnumerator<object> Coroutine;
        public StringWriter Writer;
    }

    public void Enqueue(WorkItem item)
    {
        queue.Enqueue(item);
    }

    public void Tick()
    {
        while (queue.TryDequeue(out var item))
            Start(item);

        for (var i = active.Count - 1; i >= 0; i--)
        {
            var s = active[i];

            if (s.Item.Cancel.IsCancellationRequested)
            {
                Complete(s, cancelled: true);
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

    private static string CheckPermission()
    {
        var session = MyAPIGateway.Session;
        if (session == null || session.OnlineMode == MyOnlineModeEnum.OFFLINE)
            return null;
        if (session.PromoteLevel >= MyPromoteLevel.Admin)
            return null;
        return "Multiplayer non-admin: code execution is disabled. You must be Admin or Owner to use SeMcp in multiplayer.";
    }

    private void Start(WorkItem item)
    {
        var denied = CheckPermission();
        if (denied != null)
        {
            item.Error = denied;
            item.Done.TrySetResult(true);
            return;
        }

        var writer = new StringWriter();
        var result = compiler.Compile(item.Code);

        if (!result.Success)
        {
            item.Error = result.ErrorOutput;
            item.Done.TrySetResult(true);
            return;
        }

        try
        {
            var type = result.Assembly?.GetType("__REPL__");
            var method = type?.GetMethod("Run");
            if (method == null)
            {
                item.Error = "Failed to find __REPL__.Run in compiled assembly";
                item.Done.TrySetResult(true);
                return;
            }

            var run = (Func<TextWriter, IEnumerable<object>>)Delegate.CreateDelegate(
                typeof(Func<TextWriter, IEnumerable<object>>), method);

            active.Add(new ActiveScript
            {
                Item = item,
                Coroutine = run(writer).GetEnumerator(),
                Writer = writer
            });
        }
        catch (Exception ex)
        {
            item.Error = FormatException(ex);
            item.Done.TrySetResult(true);
        }
    }

    private static void Complete(ActiveScript s, string error = null, bool cancelled = false)
    {
        s.Coroutine.Dispose();
        s.Item.Output = s.Writer.ToString();
        s.Item.Error = error;
        s.Item.WasCancelled = cancelled;
        s.Item.Done.TrySetResult(true);
    }

    private static string FormatException(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: not null } tie)
            ex = tie.InnerException;
        return $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
    }
}
