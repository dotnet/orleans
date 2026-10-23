namespace Orleans.Journaling.Ranges;

/// <summary>
/// Stores the range map: the authority for which partition owns each bucket, migrations in progress, and partition hosting.
/// </summary>
/// <remarks>
/// <para>
/// The store holds a sequence of versions of one map. Version 1 is the first map written; every later write is a
/// compare-and-swap from version <c>V</c> to <c>V + 1</c>, so a writer which read version <c>V</c> succeeds only if no
/// other write has intervened. Readers therefore observe a totally ordered sequence of views, and a crashed or
/// duplicated writer cannot lose another writer's change.
/// </para>
/// <para>
/// Implementations are linearizable: a read returns the latest completed write, and a write that completed is
/// durable. Implementations validate written maps with <see cref="RangeMap.Validate"/>. A write whose outcome cannot be
/// determined, for example because a later write superseded it before its reply arrived, throws; the caller reads the
/// map to continue.
/// </para>
/// </remarks>
public interface IRangeMetaStore
{
    /// <summary>
    /// Reads the current map.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current map, or <see langword="null"/> if no map has been written.</returns>
    ValueTask<RangeMapSnapshot?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="next"/> as version <paramref name="expectedVersion"/> + 1 if the stored version is
    /// <paramref name="expectedVersion"/>.
    /// </summary>
    /// <param name="next">The new map.</param>
    /// <param name="expectedVersion">The version the caller read, or 0 to write the first map.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// Whether the map was written, and the stored map after the call: the written map if it was written, otherwise
    /// the map which prevented the write.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="next"/> is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedVersion"/> is negative.</exception>
    ValueTask<RangeMapWriteResult> TryWriteAsync(RangeMap next, long expectedVersion, CancellationToken cancellationToken);
}
