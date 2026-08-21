using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

internal abstract class DeferredMethodInvocation : DurableTask
{
    private readonly TaskCompletionSource<DurableTaskResponse> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DurableExecutionContext? _context;
    private ExecutionContext? _executionContext;
    private Action? _moveNextAction;
    private int _started;

    public Action MoveNextAction => _moveNextAction ??= MoveNext;
    public void CaptureExecutionContext() => _executionContext = ExecutionContext.Capture();

    private void MoveNext()
    {
        var executionContext = _executionContext;
        _executionContext = null;
        if (executionContext is null)
        {
            MoveNextInContext();
        }
        else
        {
            ExecutionContext.Run(
                executionContext,
                static state => ((DeferredMethodInvocation)state!).MoveNextInContext(),
                this);
        }
    }

    private void MoveNextInContext()
    {
        using var scope = DurableExecutionContext.Enter(_context
            ?? throw new InvalidOperationException("The deferred durable task has not started."));
        MoveNextCore();
    }

    protected abstract void MoveNextCore();
    protected void Complete(DurableTaskResponse response)
    {
        _executionContext = null;
        _completion.TrySetResult(response);
    }

    protected internal sealed override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A deferred durable task definition can execute only once.");
        }

        _context = context;
        MoveNextInContext();
        return new(_completion.Task);
    }
}

internal abstract class VoidDurableTaskMethodInvocation : DeferredMethodInvocation
{
    public abstract void SetResult();
    public abstract void SetException(Exception exception);
}

internal sealed class VoidDurableTaskMethodInvocation<TStateMachine> : VoidDurableTaskMethodInvocation
    where TStateMachine : IAsyncStateMachine
{
    private TStateMachine _stateMachine = default!;
    public void SetStateMachine(TStateMachine stateMachine) => _stateMachine = stateMachine;
    protected override void MoveNextCore() => _stateMachine.MoveNext();
    public override void SetResult() => Complete(DurableTaskResponse.Completed);
    public override void SetException(Exception exception) => Complete(DurableTaskResponse.FromException(exception));
}

internal abstract class DurableTaskMethodInvocation<TResult> : DurableTask<TResult>
{
    private readonly TaskCompletionSource<DurableTaskResponse> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DurableExecutionContext? _context;
    private ExecutionContext? _executionContext;
    private Action? _moveNextAction;
    private int _started;

    public Action MoveNextAction => _moveNextAction ??= MoveNext;
    public void CaptureExecutionContext() => _executionContext = ExecutionContext.Capture();

    private void MoveNext()
    {
        var executionContext = _executionContext;
        _executionContext = null;
        if (executionContext is null)
        {
            MoveNextInContext();
        }
        else
        {
            ExecutionContext.Run(
                executionContext,
                static state => ((DurableTaskMethodInvocation<TResult>)state!).MoveNextInContext(),
                this);
        }
    }

    private void MoveNextInContext()
    {
        using var scope = DurableExecutionContext.Enter(_context
            ?? throw new InvalidOperationException("The deferred durable task has not started."));
        MoveNextCore();
    }

    protected abstract void MoveNextCore();
    public abstract void SetResult(TResult result);
    public abstract void SetException(Exception exception);
    protected void Complete(DurableTaskResponse response)
    {
        _executionContext = null;
        _completion.TrySetResult(response);
    }

    protected internal sealed override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A deferred durable task definition can execute only once.");
        }

        _context = context;
        MoveNextInContext();
        return new(_completion.Task);
    }
}

internal sealed class DurableTaskMethodInvocation<TResult, TStateMachine> : DurableTaskMethodInvocation<TResult>
    where TStateMachine : IAsyncStateMachine
{
    private TStateMachine _stateMachine = default!;
    public void SetStateMachine(TStateMachine stateMachine) => _stateMachine = stateMachine;
    protected override void MoveNextCore() => _stateMachine.MoveNext();
    public override void SetResult(TResult result) => Complete(DurableTaskResponse.FromResult(result));
    public override void SetException(Exception exception) => Complete(DurableTaskResponse.FromException(exception));
}
