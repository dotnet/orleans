using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace UnitTests.Runtime;

internal static class DrainTestHelpers
{
    internal static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task AwaitPhaseAsync(
        Task task,
        string phase,
        Func<string> describeState,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A failure backstop only: elapsed time is never evidence of correct ordering.
            await task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Observer drain did not reach {phase}. {describeState()}", exception);
        }
    }
}

internal sealed class DrainObserver : IGrainObserver
{
}

// Internal helper types and explicit interface implementations are not test classes.
internal class TestInvokable : IInvokable
{
    private readonly string _result;
    private int _invocationCount;

    internal TestInvokable(string result) => _result = result;

    internal TaskCompletionSource Entered { get; } = DrainTestHelpers.CreateSignal();
    internal TaskCompletionSource Release { get; } = DrainTestHelpers.CreateSignal();
    internal TaskCompletionSource Exited { get; } = DrainTestHelpers.CreateSignal();
    internal Action? OnEntered { get; set; }
    internal Func<TestInvokable, ValueTask<Response>>? Body { get; set; }
    internal ITargetHolder? TargetHolder { get; private set; }
    internal int InvocationCount => Volatile.Read(ref _invocationCount);

    object? IInvokable.GetTarget() => TargetHolder?.GetTarget();
    void IInvokable.SetTarget(ITargetHolder holder) => TargetHolder = holder;

    async ValueTask<Response> IInvokable.Invoke()
    {
        Interlocked.Increment(ref _invocationCount);
        try
        {
            OnEntered?.Invoke();
            Entered.TrySetResult();
            return await InvokeCoreAsync();
        }
        finally
        {
            // Body exit is deliberately NOT used as proof that the manager released admission.
            Exited.TrySetResult();
        }
    }

    protected virtual async ValueTask<Response> InvokeCoreAsync()
    {
        if (Body is { } body)
        {
            return await body(this);
        }

        await Release.Task;
        return Response.FromResult(_result);
    }

    protected virtual Type InterfaceType => typeof(IGrainObserver);
    Type IInvokable.GetInterfaceType() => InterfaceType;
    int IInvokable.GetArgumentCount() => 0;
    object? IInvokable.GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));
    void IInvokable.SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));
    string IInvokable.GetMethodName() => nameof(IInvokable.Invoke);
    string IInvokable.GetInterfaceName() => InterfaceType.FullName!;
    string IInvokable.GetActivityName() => $"{InterfaceType.FullName}.{nameof(IInvokable.Invoke)}";
    // IInvokable permits null metadata; these fixtures use the no-incoming-filters fast path.
    MethodInfo IInvokable.GetMethod() => null!;
    CancellationToken IInvokable.GetCancellationToken() => RequestCancellationToken;
    bool IInvokable.TryCancel() => CancelRequest();
    bool IInvokable.IsCancellable => SupportsCancellation;
    void IDisposable.Dispose() => DisposeCore();

    protected virtual CancellationToken RequestCancellationToken => CancellationToken.None;
    protected virtual bool SupportsCancellation => false;
    protected virtual bool CancelRequest() => false;
    protected virtual void DisposeCore() { }
    internal virtual void ReleaseForCleanup() => Release.TrySetResult();
}

internal sealed class CancellableTestInvokable : TestInvokable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _cancelCount;

    internal CancellableTestInvokable() : base("must be canceled") { }

    internal TaskCompletionSource CancellationObserved { get; } = DrainTestHelpers.CreateSignal();
    internal int CancelCount => Volatile.Read(ref _cancelCount);
    internal bool IsCancellationRequested => _cancellation.IsCancellationRequested;
    protected override CancellationToken RequestCancellationToken => _cancellation.Token;
    protected override bool SupportsCancellation => true;

    protected override bool CancelRequest()
    {
        Interlocked.Increment(ref _cancelCount);
        _cancellation.Cancel();
        return true;
    }

    protected override async ValueTask<Response> InvokeCoreAsync()
    {
        var canceled = DrainTestHelpers.CreateSignal();
        using var registration = _cancellation.Token.Register(() => canceled.TrySetResult());
        await canceled.Task;
        CancellationObserved.TrySetResult();
        _cancellation.Token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The request cancellation signal was completed without cancellation.");
    }

    internal override void ReleaseForCleanup()
    {
        // Only called from finally, after success-path assertions or a recorded test failure.
        _cancellation.Cancel();
        base.ReleaseForCleanup();
    }

    protected override void DisposeCore() => _cancellation.Dispose();
}

internal sealed class CancellationControlInvokable : TestInvokable
{
    internal CancellationControlInvokable(GrainId sender, CorrelationId messageId) : base("control")
    {
        Sender = sender;
        MessageId = messageId;
    }

    internal GrainId Sender { get; }
    internal CorrelationId MessageId { get; }
    internal IGrainCallCancellationExtension? CancellationTarget { get; private set; }
    internal TaskCompletionSource CancellationSent { get; } = DrainTestHelpers.CreateSignal();
    protected override Type InterfaceType => typeof(IGrainCallCancellationExtension);

    protected override async ValueTask<Response> InvokeCoreAsync()
    {
        CancellationTarget = TargetHolder?.GetComponent(typeof(IGrainCallCancellationExtension))
            as IGrainCallCancellationExtension
            ?? throw new InvalidOperationException("The real target holder did not supply its cancellation extension.");
        await CancellationTarget.CancelRequestAsync(Sender, MessageId, CancellationToken.None);
        CancellationSent.TrySetResult();
        await Release.Task;
        return Response.Completed;
    }
}

internal sealed class CallbackCompletionSource : IResponseCompletionSource
{
    private int _completionCount;

    internal TaskCompletionSource Completed { get; } = DrainTestHelpers.CreateSignal();
    internal int CompletionCount => Volatile.Read(ref _completionCount);
    internal object? Result { get; private set; }
    internal Exception? Exception { get; private set; }

    void IResponseCompletionSource.Complete(Response value)
    {
        // Copy observations immediately; never retain a possibly pooled Response.
        Exception = value.Exception;
        Result = Exception is null ? value.GetResult<object>() : null;
        Interlocked.Increment(ref _completionCount);
        Completed.TrySetResult();
    }

    void IResponseCompletionSource.Complete()
    {
        Exception = null;
        Result = null;
        Interlocked.Increment(ref _completionCount);
        Completed.TrySetResult();
    }
}
