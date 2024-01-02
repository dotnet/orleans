using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace System.Distributed.DurableTasks;

internal abstract class VoidDurableTaskMethodInvocation : DurableTask
{
    public abstract void SetResult();
    public abstract void SetException(Exception exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask"/> method.
/// </summary>
internal sealed class VoidDurableTaskMethodInvocation<TStateMachine> : VoidDurableTaskMethodInvocation, IValueTaskSource<DurableTaskResponse>
    where TStateMachine : IAsyncStateMachine
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _completion;

#pragma warning disable IDE0044 // Add readonly modifier
    private TStateMachine? _stateMachine;
#pragma warning restore IDE0044 // Add readonly modifier

    public void SetStateMachine(TStateMachine stateMachine) => _stateMachine = stateMachine;

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        Debug.Assert(_stateMachine is not null, "State machine should not be null at this point.");
        ArgumentNullException.ThrowIfNull(executionContext);

        DurableExecutionContext.SetCurrentContext(executionContext, out var previousContext);
        try
        {
            _stateMachine.MoveNext();
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }

        return new(this, _completion.Version);
    }

    public override void SetResult() => _completion.SetResult(DurableTaskResponse.Completed);
    public override void SetException(Exception exception) => _completion.SetResult(DurableTaskResponse.FromException(exception));
    public DurableTaskResponse GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal abstract class DurableTaskMethodInvocation<TResult> : DurableTask<TResult>
{
    public abstract void SetResult(TResult result);
    public abstract void SetException(Exception exception);
}

/// <summary>
/// Represents a locally-executing <see cref="DurableTask{TResult}"/> method.
/// </summary>
internal sealed class DurableTaskMethodInvocation<TResult, TStateMachine> : DurableTaskMethodInvocation<TResult>, IValueTaskSource<DurableTaskResponse>
    where TStateMachine : IAsyncStateMachine
{
    private ManualResetValueTaskSourceCore<DurableTaskResponse> _completion;

#pragma warning disable IDE0044 // Add readonly modifier
    private TStateMachine? _stateMachine;
#pragma warning restore IDE0044 // Add readonly modifier

    public void SetStateMachine(TStateMachine stateMachine) => _stateMachine = stateMachine;

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
    {
        Debug.Assert(_stateMachine is not null, "State machine should not be null at this point.");
        ArgumentNullException.ThrowIfNull(executionContext);

        DurableExecutionContext.SetCurrentContext(executionContext, out var previousContext);
        try
        {
            _stateMachine.MoveNext();
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }

        return new(this, _completion.Version);
    }

    public override void SetResult(TResult result) => _completion.SetResult(DurableTaskResponse.FromResult(result));
    public override void SetException(Exception exception) => _completion.SetException(exception);

    public DurableTaskResponse GetResult(short token) => _completion.GetResult(token);
    public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}
