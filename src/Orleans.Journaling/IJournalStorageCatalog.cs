namespace Orleans.Journaling;

/// <summary>
/// Provides catalog operations for journal storage instances.
/// </summary>
/// <remarks>
/// A catalog discovers storage identities. <see cref="IJournalStorage"/> provides storage lifecycle,
/// metadata, and data mutation operations.
/// </remarks>
public interface IJournalStorageCatalog
{
    /// <summary>
    /// Enumerates journal ids matching the supplied options.
    /// </summary>
    /// <param name="options">The listing options, or <see langword="null"/> to list all ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Matching ids in provider traversal order.</returns>
    /// <remarks>
    /// Options are read when enumeration begins. Providers fetch storage pages internally and yield matching ids
    /// as they are discovered. Advancing the enumerator can traverse multiple empty or filtered storage pages.
    /// Storage services determine request latency, retries, and internal scan work.
    /// Enumeration observes live storage; concurrent changes follow the provider's listing semantics.
    /// Callers should tolerate repeated identities during concurrent changes and start a new enumeration to
    /// discover later changes. Dispose the enumerator when stopping early.
    /// Storage and cancellation errors propagate through enumeration. Start a new enumeration after a listing error.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    IAsyncEnumerable<JournalId> ListAsync(
        ListOptions? options = null,
        CancellationToken cancellationToken = default);
}
