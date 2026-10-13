using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Shared.Plugin;

namespace Shared.Mcp;

// The builders a script's async methods run on. Compiler.RetargetBuilders points the script's reference
// to each BCL builder at the one here of the same shape, so the code the C# compiler wrote drives these
// instead. Each mirrors its BCL builder member for member, and all but the iterator and void ones keep
// that builder underneath. Two things change:
//
// - Where an await resumes. On a parallel script's worker (ScriptPump.Current) the awaiter is wrapped,
//   so the continuation goes back through the pump, whatever thread completes what was awaited.
//   Anywhere else the BCL builder gets the script's own awaiter. A SynchronizationContext couldn't do
//   this: whether an await honors one is the awaiter's choice, and ConfigureAwait(false) declines, as
//   does every awaiter of SE2's Keen tasks, which run continuations on the thread completing the task,
//   the game's main thread included. A continuation running anywhere but the worker is out of the
//   kill's reach, and its StackCheck measures against a baseline that isn't its own.
//
// - What a fault leaves behind. A script's Task counts as observed the moment it faults. Unobserved,
//   it would raise TaskScheduler.UnobservedTaskException once collected, which SE2's crash handler
//   answers by ending the session: a script that started a task and never awaited it was enough, or
//   one killed mid-step, whose abort lands in its own state machine. An async void method's exception
//   becomes its script's error on "parallel" and a log line elsewhere; the BCL would rethrow it on the
//   thread pool or the captured context, ending the process either way.
//
// C# picks a builder by return type. void, Task, Task<T> and the async iterators come here, and the
// ValueTask methods run on the Task builders (Compiler.ValueTasksOnTasks). Any other builder keeps its
// own behavior: one named by an explicit [AsyncMethodBuilder] (PoolingAsyncValueTaskMethodBuilder, a
// script's own), and SE2's Keen TaskMethodBuilder.

// ReSharper disable UnusedMember.Global
public struct ScriptTaskBuilder
{
    private AsyncTaskMethodBuilder inner;

    public static ScriptTaskBuilder Create() => new() { inner = AsyncTaskMethodBuilder.Create() };

    public Task Task => inner.Task;

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        inner.Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) => inner.SetStateMachine(stateMachine);

    public void SetResult() => inner.SetResult();

    public void SetException(Exception exception)
    {
        inner.SetException(exception);
        _ = inner.Task.Exception;   // reading it marks the fault observed
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } pump)
        {
            inner.AwaitOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new Rerouted<TAwaiter>(awaiter, pump);
        inner.AwaitOnCompleted(ref rerouted, ref stateMachine);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } pump)
        {
            inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new ReroutedCritical<TAwaiter>(awaiter, pump);
        inner.AwaitUnsafeOnCompleted(ref rerouted, ref stateMachine);
    }
}

public struct ScriptTaskBuilder<TResult>
{
    private AsyncTaskMethodBuilder<TResult> inner;

    public static ScriptTaskBuilder<TResult> Create() => new() { inner = AsyncTaskMethodBuilder<TResult>.Create() };

    public Task<TResult> Task => inner.Task;

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        inner.Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) => inner.SetStateMachine(stateMachine);

    public void SetResult(TResult result) => inner.SetResult(result);

    public void SetException(Exception exception)
    {
        inner.SetException(exception);
        _ = inner.Task.Exception;   // reading it marks the fault observed
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } pump)
        {
            inner.AwaitOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new Rerouted<TAwaiter>(awaiter, pump);
        inner.AwaitOnCompleted(ref rerouted, ref stateMachine);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } pump)
        {
            inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new ReroutedCritical<TAwaiter>(awaiter, pump);
        inner.AwaitUnsafeOnCompleted(ref rerouted, ref stateMachine);
    }
}

// Async iterators. Their faults surface through the enumerator they hand out, not a Task, so there is
// nothing to observe. Built on the Task builder rather than wrapping AsyncIteratorMethodBuilder, which
// SE1's build on .NET 10 (Pulsar Interim) can't name: it references the game's .NET Framework copy of
// Microsoft.Bcl.AsyncInterfaces beside CoreLib, and both define it. That package builds its own the
// same way; the Task is nobody's.
public struct ScriptIteratorBuilder
{
    private AsyncTaskMethodBuilder inner;

    public static ScriptIteratorBuilder Create() => new() { inner = AsyncTaskMethodBuilder.Create() };

    public void MoveNext<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        inner.Start(ref stateMachine);

    public void Complete() => inner.SetResult();

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } pump)
        {
            inner.AwaitOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new Rerouted<TAwaiter>(awaiter, pump);
        inner.AwaitOnCompleted(ref rerouted, ref stateMachine);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } pump)
        {
            inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new ReroutedCritical<TAwaiter>(awaiter, pump);
        inner.AwaitUnsafeOnCompleted(ref rerouted, ref stateMachine);
    }
}

// Async void methods. Built on the Task builder rather than wrapping AsyncVoidMethodBuilder, which
// SE2's build can't name: every game assembly ships a public type of the same full name. Keen builds
// theirs the same way; the Task is nobody's.
public struct ScriptVoidBuilder
{
    private AsyncTaskMethodBuilder inner;

    // The parallel script it was called from, if any.
    private ScriptPump pump;

    public static ScriptVoidBuilder Create() => new() { inner = AsyncTaskMethodBuilder.Create(), pump = ScriptPump.Current };

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        inner.Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) => inner.SetStateMachine(stateMachine);

    public void SetResult() => inner.SetResult();

    // Thrown on its script's worker as it was thrown here: the script's error. A pump closed by then
    // sends it to the thread pool and the log, or drops it with a killed script's other continuations.
    public void SetException(Exception exception)
    {
        inner.SetResult();
        if (pump != null)
            pump.Resume(ExceptionDispatchInfo.Capture(exception).Throw)();
        else
            Common.Logger.Warning($"a script's async void method threw: {exception.GetType().Name}: {exception.Message}");
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } current)
        {
            inner.AwaitOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new Rerouted<TAwaiter>(awaiter, current);
        inner.AwaitOnCompleted(ref rerouted, ref stateMachine);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        if (ScriptPump.Current is not { } current)
        {
            inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
            return;
        }
        var rerouted = new ReroutedCritical<TAwaiter>(awaiter, current);
        inner.AwaitUnsafeOnCompleted(ref rerouted, ref stateMachine);
    }
}

// The awaiter a builder gets in place of the script's: when the continuation runs is still the original
// awaiter's to decide; where it runs becomes the pump's. Each calls the original through its own
// constraint, so nothing is boxed.
// ReSharper disable once StructCanBeMadeReadOnly
internal struct Rerouted<TAwaiter>(TAwaiter awaiter, ScriptPump pump) : INotifyCompletion
    where TAwaiter : INotifyCompletion
{
    public void OnCompleted(Action continuation) => awaiter.OnCompleted(pump.Resume(continuation));
}

// ReSharper disable once StructCanBeMadeReadOnly
internal struct ReroutedCritical<TAwaiter>(TAwaiter awaiter, ScriptPump pump) : ICriticalNotifyCompletion
    where TAwaiter : ICriticalNotifyCompletion
{
    public void OnCompleted(Action continuation) => awaiter.OnCompleted(pump.Resume(continuation));

    public void UnsafeOnCompleted(Action continuation) => awaiter.UnsafeOnCompleted(pump.Resume(continuation));
}
