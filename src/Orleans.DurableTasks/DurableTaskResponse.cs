using System.Runtime.ExceptionServices;

namespace Orleans.DurableTasks;

/// <summary>Identifies the execution status of a durable task.</summary>
public enum DurableTaskStatus
{
    /// <summary>No status is available.</summary>
    None,
    /// <summary>The task is not terminal.</summary>
    Pending,
    /// <summary>The task completed successfully.</summary>
    CompletedSuccessfully,
    /// <summary>The task was canceled.</summary>
    Canceled,
    /// <summary>The task failed.</summary>
    Failed,
}

/// <summary>Identifies the kind of durable task observation.</summary>
public enum DurableTaskResponseKind
{
    /// <summary>No observation is available.</summary>
    None,
    /// <summary>The task is pending.</summary>
    Pending,
    /// <summary>The caller is subscribed to a future terminal response.</summary>
    Subscribed,
    /// <summary>The task completed successfully.</summary>
    CompletedSuccessfully,
    /// <summary>The task was canceled.</summary>
    Canceled,
    /// <summary>The task failed.</summary>
    Failed,
}

/// <summary>Represents a durable task observation.</summary>
public abstract class DurableTaskResponse
{
    internal DurableTaskResponse() { }

    /// <summary>Gets the response for a successful task without a result value.</summary>
    public static DurableTaskResponse Completed => SuccessDurableTaskResponse.Instance;
    /// <summary>Gets the pending response.</summary>
    public static DurableTaskResponse Pending => PendingDurableTaskResponse.Instance;
    /// <summary>Gets the subscribed response.</summary>
    public static DurableTaskResponse Subscribed => SubscribedDurableTaskResponse.Instance;
    /// <summary>Gets a canceled response with a default cancellation exception.</summary>
    public static DurableTaskResponse Canceled => CanceledDurableTaskResponse.Instance;
    /// <summary>Creates a canceled response.</summary>
    public static DurableTaskResponse FromCanceled(OperationCanceledException exception) => new CanceledDurableTaskResponse(exception);
    /// <summary>Creates a failed or canceled response from an exception.</summary>
    public static DurableTaskResponse FromException(Exception exception)
        => exception is OperationCanceledException canceled ? FromCanceled(canceled) : new ExceptionDurableTaskResponse(exception);
    /// <summary>Creates a successful response containing <paramref name="value"/>.</summary>
    public static DurableTaskResponse<TResult> FromResult<TResult>(TResult value) => new(value);

    /// <summary>Gets the response kind.</summary>
    public abstract DurableTaskResponseKind ResponseKind { get; }
    /// <summary>Gets the untyped result, or throws if the task did not complete successfully.</summary>
    public abstract object? Result { get; }
    /// <summary>Gets the declared result type, if any.</summary>
    public virtual Type? ResultType => null;
    /// <summary>Gets the terminal exception, if any.</summary>
    public abstract Exception? Exception { get; }
    /// <summary>Returns the result as <typeparamref name="T"/>, or throws if it is unavailable or incompatible.</summary>
    public abstract T GetResult<T>();

    /// <summary>Gets a value indicating whether the response is terminal.</summary>
    public bool IsCompleted => ResponseKind is DurableTaskResponseKind.CompletedSuccessfully
        or DurableTaskResponseKind.Canceled
        or DurableTaskResponseKind.Failed;

    /// <summary>Gets the execution status represented by this response.</summary>
    public DurableTaskStatus Status => ResponseKind switch
    {
        DurableTaskResponseKind.None => DurableTaskStatus.None,
        DurableTaskResponseKind.Pending or DurableTaskResponseKind.Subscribed => DurableTaskStatus.Pending,
        DurableTaskResponseKind.CompletedSuccessfully => DurableTaskStatus.CompletedSuccessfully,
        DurableTaskResponseKind.Canceled => DurableTaskStatus.Canceled,
        DurableTaskResponseKind.Failed => DurableTaskStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown response kind '{ResponseKind}'."),
    };

    internal void EnsureSuccessfulCompletion()
    {
        if (!IsCompleted)
        {
            throw new InvalidOperationException("The durable task has not completed.");
        }

        if (Exception is { } exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    /// <summary>Creates the standard exception for access to an incomplete response.</summary>
    protected static InvalidOperationException Incomplete()
        => new("The durable task has not completed.");
}

/// <summary>Represents a successful durable task without a result value.</summary>
public sealed class SuccessDurableTaskResponse : DurableTaskResponse
{
    /// <summary>Gets the shared successful response.</summary>
    public static SuccessDurableTaskResponse Instance { get; } = new();
    private SuccessDurableTaskResponse() { }
    /// <inheritdoc />
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.CompletedSuccessfully;
    /// <inheritdoc />
    public override object? Result => null;
    /// <inheritdoc />
    public override Exception? Exception => null;
    /// <inheritdoc />
    public override T GetResult<T>() => throw new InvalidOperationException("The completed task has no result value.");
}

/// <summary>Represents a successful durable task with a result value.</summary>
public sealed class DurableTaskResponse<TResult>(TResult result) : DurableTaskResponse
{
    /// <summary>Gets the typed result.</summary>
    public TResult TypedResult => result;
    /// <inheritdoc />
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.CompletedSuccessfully;
    /// <inheritdoc />
    public override object? Result => result;
    /// <inheritdoc />
    public override Type ResultType => typeof(TResult);
    /// <inheritdoc />
    public override Exception? Exception => null;
    /// <inheritdoc />
    public override T GetResult<T>()
    {
        if (result is T value)
        {
            return value;
        }

        if (result is null
            && default(T) is null
            && typeof(T).IsAssignableFrom(typeof(TResult)))
        {
            return default!;
        }

        throw new InvalidCastException($"The durable task result is '{typeof(TResult)}', not '{typeof(T)}'.");
    }
}

/// <summary>Represents a failed durable task.</summary>
public sealed class ExceptionDurableTaskResponse : DurableTaskResponse
{
    /// <summary>Initializes a response for <paramref name="exception"/>.</summary>
    public ExceptionDurableTaskResponse(Exception exception)
        => Exception = exception ?? throw new ArgumentNullException(nameof(exception));

    /// <inheritdoc />
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Failed;
    /// <inheritdoc />
    public override object? Result => GetResult<object?>();
    /// <inheritdoc />
    public override Exception Exception { get; }
    /// <inheritdoc />
    public override T GetResult<T>()
    {
        ExceptionDispatchInfo.Capture(Exception).Throw();
        return default!;
    }
}

/// <summary>Represents a canceled durable task.</summary>
public sealed class CanceledDurableTaskResponse : DurableTaskResponse
{
    /// <summary>Gets the shared canceled response.</summary>
    public static CanceledDurableTaskResponse Instance { get; } = new(new OperationCanceledException());

    /// <summary>Initializes a response for <paramref name="exception"/>.</summary>
    public CanceledDurableTaskResponse(OperationCanceledException exception)
        => Exception = exception ?? throw new ArgumentNullException(nameof(exception));

    /// <inheritdoc />
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Canceled;
    /// <inheritdoc />
    public override object? Result => GetResult<object?>();
    /// <inheritdoc />
    public override OperationCanceledException Exception { get; }
    /// <inheritdoc />
    public override T GetResult<T>()
    {
        ExceptionDispatchInfo.Capture(Exception).Throw();
        return default!;
    }
}

/// <summary>Represents a pending durable task.</summary>
public sealed class PendingDurableTaskResponse : DurableTaskResponse
{
    /// <summary>Gets the shared pending response.</summary>
    public static PendingDurableTaskResponse Instance { get; } = new();
    private PendingDurableTaskResponse() { }
    /// <inheritdoc />
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Pending;
    /// <inheritdoc />
    public override object? Result => throw Incomplete();
    /// <inheritdoc />
    public override Exception? Exception => null;
    /// <inheritdoc />
    public override T GetResult<T>() => throw Incomplete();
}

/// <summary>Represents a subscription to a future durable task response.</summary>
public sealed class SubscribedDurableTaskResponse : DurableTaskResponse
{
    /// <summary>Gets the shared subscribed response.</summary>
    public static SubscribedDurableTaskResponse Instance { get; } = new();
    private SubscribedDurableTaskResponse() { }
    /// <inheritdoc />
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Subscribed;
    /// <inheritdoc />
    public override object? Result => throw Incomplete();
    /// <inheritdoc />
    public override Exception? Exception => null;
    /// <inheritdoc />
    public override T GetResult<T>() => throw Incomplete();
}
