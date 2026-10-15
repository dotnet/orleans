using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// A request queued on an activation by <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The request receives its own response, so queuing a request allocates only this object and its message.
/// Cancellation applies while the request is queued. Once the callback starts, the task reflects the callback's outcome.
/// When a call filter completes the invocation without running the callback, the task reflects the filter's outcome.
/// Call filters observe the request as <see cref="IGrainRequestInvoker.RunRequestAsync"/>.
/// </remarks>
internal sealed class GrainRequest<TState, TResult> : TaskCompletionSource<TResult>, IGrainRequestInvoker, ILocalRequest
{
    private readonly Func<TState, CancellationToken, Task> _callback;
    private readonly TState _state;
    private readonly CancellationTokenRegistration _registration;

    public GrainRequest(Func<TState, CancellationToken, Task> callback, TState state, CancellationToken cancellationToken)
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        _callback = callback;
        _state = state;
        _registration = cancellationToken.UnsafeRegister(
            static (state, cancellationToken) => ((GrainRequest<TState, TResult>)state!).TrySetCanceled(cancellationToken),
            this);
    }

    public ValueTask<Response> Invoke()
    {
        var cancellationToken = _registration.Token;
        if (Task.IsCompleted)
        {
            // The request was canceled while it was queued.
            return new(Response.Completed);
        }

        // Unregistering succeeds while cancellation is still running earlier callbacks, so check the token too.
        if (cancellationToken.CanBeCanceled && (!_registration.Unregister() || cancellationToken.IsCancellationRequested))
        {
            TrySetCanceled(cancellationToken);
            return new(Response.Completed);
        }

        Task task;
        try
        {
            task = _callback(_state, cancellationToken);
        }
        catch (Exception exception)
        {
            TrySetException(exception);
            return new(Response.FromException(exception));
        }

        if (task.IsCompletedSuccessfully)
        {
            TrySetResult(GetResult(task));
            return new(Response.Completed);
        }

        return AwaitCallbackAsync(task);
    }

    private async ValueTask<Response> AwaitCallbackAsync(Task task)
    {
        try
        {
            await task;
            TrySetResult(GetResult(task));
            return Response.Completed;
        }
        catch (OperationCanceledException exception) when (task.IsCanceled)
        {
            TrySetCanceled(exception.CancellationToken);
            return Response.FromException(exception);
        }
        catch (Exception exception)
        {
            TrySetException(exception);
            return Response.FromException(exception);
        }
    }

    private static TResult GetResult(Task task) => task is Task<TResult> typed ? typed.Result : default!;

    bool ILocalRequest.Complete(Response response)
    {
        var task = Task;
        if (task.IsCompleted)
        {
            // The callback completed the task, so the task reports the callback's failure but not a later filter's failure.
            return response.Exception is not { } exception
                || task.IsCanceled && exception is OperationCanceledException
                || task.IsFaulted && ReferenceEquals(task.Exception!.InnerException, exception);
        }

        // A call filter completed the invocation without running the callback.
        // Cancellation can complete the task first, in which case the task does not report the filter's failure.
        _registration.Dispose();
        if (response.Exception is { } filterException)
        {
            return TrySetException(filterException);
        }

        TrySetResult(response.Result is TResult result ? result : default!);
        return true;
    }


    public object? GetTarget() => this;

    public void SetTarget(ITargetHolder holder)
    {
    }

    // Declared for call filters, which inspect the interface and implementation methods.
    Task IGrainRequestInvoker.RunRequestAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public void SetArgument(int index, object? value) => throw new ArgumentOutOfRangeException(nameof(index));

    public string GetMethodName() => nameof(IGrainRequestInvoker.RunRequestAsync);

    public string GetInterfaceName() => nameof(IGrainRequestInvoker);

    public string GetActivityName() => $"{nameof(IGrainRequestInvoker)}/{nameof(IGrainRequestInvoker.RunRequestAsync)}";

    public MethodInfo GetMethod() => IGrainRequestInvoker.MethodInfo;

    public Type GetInterfaceType() => typeof(IGrainRequestInvoker);

    public TimeSpan? GetDefaultResponseTimeout() => null;

    public void Dispose()
    {
    }

    public override string ToString() => $"Request '{GetActivityName()}'";
}

/// <summary>
/// The interface which call filters observe for requests queued by <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>.
/// </summary>
internal interface IGrainRequestInvoker
{
    internal static readonly MethodInfo MethodInfo = typeof(IGrainRequestInvoker).GetMethod(nameof(RunRequestAsync), BindingFlags.Instance | BindingFlags.Public)!;

    /// <summary>
    /// Runs the request callback.
    /// </summary>
    Task RunRequestAsync(CancellationToken cancellationToken = default);
}
