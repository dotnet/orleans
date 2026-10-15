using System.Reflection;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// A request queued on an activation by <see cref="GrainBaseExtensions.RunRequestAsync(IGrainBase, Func{CancellationToken, Task}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// The request is its own completion source, so queuing a request allocates only this object and its message.
/// Cancellation applies while the request is queued. Once the callback starts, the task reflects the callback's outcome.
/// When a call filter completes the invocation without running the callback, the task reflects the filter's outcome.
/// Call filters observe the request as <see cref="IGrainRequestInvoker.RunRequestAsync"/> unless it is created with
/// <see cref="Create"/> and named by <see cref="GrainRequestOptions"/>.
/// </remarks>
internal class GrainRequest<TState, TResult> : TaskCompletionSource<TResult>, IInvokable, IGrainRequestInvoker, ILocalRequest
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

    /// <summary>
    /// Creates a request, allocating storage for the names and arguments in <paramref name="options"/> only when they are set.
    /// </summary>
    public static GrainRequest<TState, TResult> Create(
        Func<TState, CancellationToken, Task> callback,
        TState state,
        in GrainRequestOptions options,
        CancellationToken cancellationToken) =>
        options.InterfaceName is null && options.MethodName is null && options.Arguments is null
            ? new GrainRequest<TState, TResult>(callback, state, cancellationToken)
            : new NamedGrainRequest<TState, TResult>(callback, state, options, cancellationToken);

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

    public virtual int GetArgumentCount() => 0;

    public virtual object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public virtual void SetArgument(int index, object? value) => throw new ArgumentOutOfRangeException(nameof(index));

    public virtual string GetMethodName() => nameof(IGrainRequestInvoker.RunRequestAsync);

    public virtual string GetInterfaceName() => nameof(IGrainRequestInvoker);

    public virtual string GetActivityName() => $"{nameof(IGrainRequestInvoker)}/{nameof(IGrainRequestInvoker.RunRequestAsync)}";

    public MethodInfo GetMethod() => IGrainRequestInvoker.MethodInfo;

    public Type GetInterfaceType() => typeof(IGrainRequestInvoker);

    public TimeSpan? GetDefaultResponseTimeout() => null;

    public void Dispose()
    {
    }

    public override string ToString() => $"Request '{_callback.Target}.{_callback.Method}'";
}

/// <summary>
/// A <see cref="GrainRequest{TState, TResult}"/> which describes itself to call filters using the names and arguments
/// in <see cref="GrainRequestOptions"/>. Each unset name falls back to <see cref="IGrainRequestInvoker"/>'s.
/// </summary>
internal sealed class NamedGrainRequest<TState, TResult> : GrainRequest<TState, TResult>
{
    private readonly string? _interfaceName;
    private readonly string? _methodName;
    private readonly object?[]? _arguments;

    public NamedGrainRequest(Func<TState, CancellationToken, Task> callback, TState state, in GrainRequestOptions options, CancellationToken cancellationToken)
        : base(callback, state, cancellationToken)
    {
        _interfaceName = options.InterfaceName;
        _methodName = options.MethodName;
        _arguments = options.Arguments;
    }

    public override int GetArgumentCount() => _arguments?.Length ?? 0;

    public override object? GetArgument(int index) => _arguments is { } arguments ? arguments[index] : base.GetArgument(index);

    public override void SetArgument(int index, object? value)
    {
        if (_arguments is { } arguments)
        {
            arguments[index] = value;
        }
        else
        {
            base.SetArgument(index, value);
        }
    }

    public override string GetMethodName() => _methodName ?? base.GetMethodName();

    public override string GetInterfaceName() => _interfaceName ?? base.GetInterfaceName();

    public override string GetActivityName() => $"{GetInterfaceName()}/{GetMethodName()}";
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
/// A local-only request whose task reports its outcome to the code which queued it.
/// </summary>
internal interface ILocalRequest
{
    /// <summary>
    /// Completes the request with <paramref name="response"/> if the request has not already completed.
    /// </summary>
    /// <returns><see langword="true"/> if the request's task reports the response's exception, if any.</returns>
    bool Complete(Response response);

    /// <summary>
    /// Completes the request with <paramref name="exception"/> if the request has not already completed.
    /// </summary>
    void Reject(Exception exception) => Complete(Response.FromException(exception));
}
