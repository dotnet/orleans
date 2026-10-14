using System.Reflection;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// A request queued on an activation by <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The request is its own completion source, so queuing a request allocates only this object and its message.
/// Cancellation applies while the request is queued. Once the callback starts, the task reflects the callback's outcome.
/// </remarks>
internal sealed class GrainRequest<TState, TResult> : TaskCompletionSource<TResult>, IInvokable, IGrainRequestInvoker, ILocalRequest
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
        if (Task.IsCompleted || cancellationToken.CanBeCanceled && !_registration.Unregister())
        {
            // The request was canceled while it was queued.
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

    void ILocalRequest.Reject(Exception exception)
    {
        _registration.Dispose();
        TrySetException(exception);
    }

    public object? GetTarget() => this;

    public void SetTarget(ITargetHolder holder)
    {
    }

    // Declared for call filters, which inspect the interface and implementation methods.
    Task IGrainRequestInvoker.RunRequestAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

    public int GetArgumentCount() => 0;

    public object? GetArgument(int index) => throw new InvalidOperationException();

    public void SetArgument(int index, object? value) => throw new InvalidOperationException();

    public string GetMethodName() => nameof(IGrainRequestInvoker.RunRequestAsync);

    public string GetInterfaceName() => nameof(IGrainRequestInvoker);

    public string GetActivityName() => $"{nameof(IGrainRequestInvoker)}/{nameof(IGrainRequestInvoker.RunRequestAsync)}";

    public MethodInfo GetMethod() => IGrainRequestInvoker.MethodInfo;

    public Type GetInterfaceType() => typeof(IGrainRequestInvoker);

    public TimeSpan? GetDefaultResponseTimeout() => null;

    public void Dispose()
    {
    }

    public override string ToString() => $"Request '{_callback.Target}.{_callback.Method}'";
}

/// <summary>
/// The interface which call filters observe for requests queued by <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>.
/// </summary>
internal interface IGrainRequestInvoker : IAddressable
{
    internal static readonly GrainInterfaceType InterfaceType = GrainInterfaceType.Create("Orleans.Runtime.IGrainRequestInvoker");
    internal static readonly MethodInfo MethodInfo = typeof(IGrainRequestInvoker).GetMethod(nameof(RunRequestAsync), BindingFlags.Instance | BindingFlags.Public)!;

    /// <summary>
    /// Runs the request callback.
    /// </summary>
    [Alias("6D1E58B4")]
    Task RunRequestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A local-only request which must be notified when its activation drops it without running it.
/// </summary>
internal interface ILocalRequest
{
    /// <summary>
    /// Completes the request with <paramref name="exception"/>.
    /// </summary>
    void Reject(Exception exception);
}
