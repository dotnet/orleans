namespace Orleans.Journaling;

/// <summary>
/// Provides catalog operations for journal storage instances.
/// </summary>
/// <remarks>
/// A catalog discovers storage identities and optional metadata snapshots. <see cref="IJournalStorage"/> provides storage lifecycle,
/// metadata, and data mutation operations.
/// </remarks>
public interface IJournalStorageCatalog
{
    /// <summary>
    /// Enumerates journal entries matching the supplied options.
    /// </summary>
    /// <param name="options">The listing options, or <see langword="null"/> to list all entries without metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Matching entries in provider traversal order.</returns>
    /// <remarks>
    /// Options are snapshotted when enumeration begins. The raw prefix and inclusive lower and upper bounds all apply.
    /// Results are not guaranteed to be sorted; callers requiring ordering must sort the selected ids.
    /// <see cref="JournalCatalogListOptions.IncludeMetadata"/> requests complete metadata snapshots available from the listing.
    /// Entries carry <see langword="null"/> metadata when the provider cannot project it or it was not requested.
    /// Providers fetch storage pages internally and yield matching entries
    /// as they are discovered. Advancing the enumerator can traverse multiple empty or filtered storage pages.
    /// Storage services determine request latency, retries, and internal scan work.
    /// Enumeration observes live storage; concurrent changes follow the provider's listing semantics.
    /// Callers should tolerate repeated identities during concurrent changes and start a new enumeration to
    /// discover later changes. Deduplicate by <see cref="JournalCatalogEntry.Id"/> when unique identities
    /// are required; repeated entries can carry different metadata versions. Dispose the enumerator when stopping early.
    /// Storage and cancellation errors propagate through enumeration. Start a new enumeration after a listing error.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        JournalCatalogListOptions? options = null,
        CancellationToken cancellationToken = default);
}
