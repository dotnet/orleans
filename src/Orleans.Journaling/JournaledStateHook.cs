namespace Orleans.Journaling;

/// <summary>
/// Adapts synchronous and asynchronous delegates to journal operation hooks.
/// </summary>
/// <remarks>
/// When both delegates for a phase are supplied, the synchronous delegate runs first.
/// Asynchronous delegates await their I/O outcome before completing their returned value task.
/// Custom <see cref="IJournaledStateHook"/> implementations can carry feature identity and state
/// for inspection and deduplication in the owner's hook list.
/// </remarks>
public sealed class JournaledStateHook : IJournaledStateHook
{
    /// <summary>Gets the synchronous prerequisite callback.</summary>
    public Action<JournaledStateOperation, CancellationToken>? BeforeOperation { get; init; }

    /// <summary>Gets the asynchronous prerequisite callback.</summary>
    public Func<JournaledStateOperation, CancellationToken, ValueTask>? BeforeOperationAsync { get; init; }

    /// <summary>Gets the synchronous completion callback.</summary>
    public Action<JournaledStateOperation, CancellationToken>? AfterOperation { get; init; }

    /// <summary>Gets the asynchronous completion callback.</summary>
    public Func<JournaledStateOperation, CancellationToken, ValueTask>? AfterOperationAsync { get; init; }

    ValueTask IJournaledStateHook.BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        BeforeOperation?.Invoke(operation, cancellationToken);
        return BeforeOperationAsync?.Invoke(operation, cancellationToken) ?? default;
    }

    ValueTask IJournaledStateHook.AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        AfterOperation?.Invoke(operation, cancellationToken);
        return AfterOperationAsync?.Invoke(operation, cancellationToken) ?? default;
    }
}
