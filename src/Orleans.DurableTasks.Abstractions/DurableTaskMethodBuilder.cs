using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>Builds compiler-lowered asynchronous methods which return <see cref="DurableTask"/>.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Compiler method builders are mutable protocol values without an equality contract.")]
public struct DurableTaskMethodBuilder
{
    private VoidDurableTaskMethodInvocation? _invocation;

    private readonly VoidDurableTaskMethodInvocation Invocation
        => _invocation ?? throw new InvalidOperationException("The durable task builder has not started.");

    /// <summary>Gets the deferred durable task definition.</summary>
    public readonly DurableTask Task => Invocation;

    /// <summary>Creates a builder.</summary>
    public static DurableTaskMethodBuilder Create() => new();

    /// <summary>Captures <paramref name="stateMachine"/> without executing it.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        var invocation = new VoidDurableTaskMethodInvocation<TStateMachine>();
        _invocation = invocation;
        invocation.SetStateMachine(stateMachine);
    }

    /// <inheritdoc />
    public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        Debug.Fail("The durable task builder stores its own boxed state machine.");
    }

    /// <summary>Completes the definition with an exception.</summary>
    public readonly void SetException(Exception exception) => Invocation.SetException(exception);

    /// <summary>Completes the definition successfully.</summary>
    public readonly void SetResult() => Invocation.SetResult();

    /// <summary>Registers the next state-machine step with a safe awaiter.</summary>
    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var invocation = Invocation;
        invocation.CaptureExecutionContext();
        awaiter.OnCompleted(invocation.MoveNextAction);
    }

    /// <summary>Registers the next state-machine step with a critical awaiter.</summary>
    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var invocation = Invocation;
        invocation.CaptureExecutionContext();
        awaiter.UnsafeOnCompleted(invocation.MoveNextAction);
    }
}

/// <summary>Builds compiler-lowered asynchronous methods which return <see cref="DurableTask{TResult}"/>.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Compiler method builders are mutable protocol values without an equality contract.")]
public struct DurableTaskMethodBuilder<TResult>
{
    private DurableTaskMethodInvocation<TResult>? _invocation;

    private readonly DurableTaskMethodInvocation<TResult> Invocation
        => _invocation ?? throw new InvalidOperationException("The durable task builder has not started.");

    /// <summary>Gets the deferred durable task definition.</summary>
    public readonly DurableTask<TResult> Task => Invocation;

    /// <summary>Creates a builder.</summary>
    public static DurableTaskMethodBuilder<TResult> Create() => new();

    /// <summary>Captures <paramref name="stateMachine"/> without executing it.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        var invocation = new DurableTaskMethodInvocation<TResult, TStateMachine>();
        _invocation = invocation;
        invocation.SetStateMachine(stateMachine);
    }

    /// <inheritdoc />
    public readonly void SetStateMachine(IAsyncStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        Debug.Fail("The durable task builder stores its own boxed state machine.");
    }

    /// <summary>Completes the definition with an exception.</summary>
    public readonly void SetException(Exception exception) => Invocation.SetException(exception);

    /// <summary>Completes the definition successfully with <paramref name="result"/>.</summary>
    public readonly void SetResult(TResult result) => Invocation.SetResult(result);

    /// <summary>Registers the next state-machine step with a safe awaiter.</summary>
    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var invocation = Invocation;
        invocation.CaptureExecutionContext();
        awaiter.OnCompleted(invocation.MoveNextAction);
    }

    /// <summary>Registers the next state-machine step with a critical awaiter.</summary>
    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
    {
        var invocation = Invocation;
        invocation.CaptureExecutionContext();
        awaiter.UnsafeOnCompleted(invocation.MoveNextAction);
    }
}
