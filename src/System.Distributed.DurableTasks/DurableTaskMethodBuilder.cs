using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask"/>.
/// </summary>
public struct DurableTaskMethodBuilder
{
    private VoidDurableTaskMethodInvocation _taskSource;

    public readonly DurableTask Task => _taskSource;

    public static DurableTaskMethodBuilder Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        // The sequence of operations here is significant: since the DurableTaskMethodBuilder is held as a field on the state machine,
        // we must perform any necessary mutations to the builder before copying it into the box.
        var taskSource = new VoidDurableTaskMethodInvocation<TStateMachine>();
        _taskSource = taskSource;
        taskSource.SetStateMachine(stateMachine);
    }

    public readonly void SetStateMachine(IAsyncStateMachine stateMachine) => SetStateMachineCore(stateMachine);

    public readonly void SetException(Exception exception)
    {
        _taskSource.SetException(exception);
    }

    public readonly void SetResult()
    {
        _taskSource.SetResult();
    }

    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }

    internal static void SetStateMachineCore(IAsyncStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);

        // SetStateMachine was originally needed in order to store the boxed state machine reference into
        // the boxed copy.  Now that a normal box is no longer used, SetStateMachine is also legacy.  We need not
        // do anything here, and thus assert to ensure we're not calling this from our own implementations.
        Debug.Fail("SetStateMachine should not be used.");
    }
}

/// <summary>
/// Async method builder for methods which return <see cref="DurableTask{TResult}"/>.
/// </summary>
public struct DurableTaskMethodBuilder<TResult>
{
    private DurableTaskMethodInvocation<TResult> _taskSource;

    public readonly DurableTask<TResult> Task => _taskSource;

    public static DurableTaskMethodBuilder<TResult> Create() => new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // Box the state machine and do not start it.
        // Instead, the state machine will be started once the resulting task is awaited (not when the method is called directly)
        // The sequence of operations here is significant: since the DurableTaskMethodBuilder is held as a field on the state machine,
        // we must perform any necessary mutations to the builder before copying it into the box.
        var taskSource = new DurableTaskMethodInvocation<TResult, TStateMachine>();
        _taskSource = taskSource;
        taskSource.SetStateMachine(stateMachine);
    }

    public readonly void SetStateMachine(IAsyncStateMachine stateMachine) => DurableTaskMethodBuilder.SetStateMachineCore(stateMachine);

    public readonly void SetException(Exception exception)
    {
        _taskSource.SetException(exception);
    }

    public readonly void SetResult(TResult result)
    {
        _taskSource.SetResult(result);
    }

    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.OnCompleted(stateMachine.MoveNext);
    }

    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        awaiter.UnsafeOnCompleted(stateMachine.MoveNext);
    }
}
