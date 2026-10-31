namespace Orleans.Journaling.Ranges;

/// <summary>
/// Stores the range map in memory.
/// </summary>
/// <remarks>
/// The map lives only as long as this instance, so the store suits tests and single-process development. Every
/// silo of a cluster must share the same instance.
/// </remarks>
public sealed class InMemoryRangeMetaStore : IRangeMetaStore
{
    private readonly Lock _lock = new();
    private RangeMapSnapshot? _current;

    /// <inheritdoc/>
    public ValueTask<RangeMapSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return new(_current);
        }
    }

    /// <inheritdoc/>
    public ValueTask<RangeMapWriteResult> TryWriteAsync(RangeMap next, long expectedVersion, CancellationToken cancellationToken)
    {
        RangeMetaStoreArguments.Validate(next, expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if ((_current?.Version ?? 0) != expectedVersion)
            {
                return new(new RangeMapWriteResult(false, _current));
            }

            _current = new RangeMapSnapshot(expectedVersion + 1, next);
            return new(new RangeMapWriteResult(true, _current));
        }
    }
}

internal static class RangeMetaStoreArguments
{
    public static void Validate(RangeMap next, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        try
        {
            next.Validate();
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(exception.Message, nameof(next), exception);
        }
    }
}
