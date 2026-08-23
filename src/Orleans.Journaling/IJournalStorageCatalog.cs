namespace Orleans.Journaling;

/// <summary>
/// Provides catalog operations for journal storage instances.
/// </summary>
/// <remarks>
/// A catalog only discovers storage identities. Storage lifecycle, metadata, and data mutation
/// operations remain on <see cref="IJournalStorage"/>.
/// </remarks>
public interface IJournalStorageCatalog
{
    /// <summary>
    /// Lists journal ids which match <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The journal id prefix, or the default value to list all ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Matching ids in lexicographic <see cref="JournalId.Value"/> order.</returns>
    IAsyncEnumerable<JournalId> ListAsync(JournalId prefix = default, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides bounded, resumable catalog operations for journal storage instances.
/// </summary>
public interface IPagedJournalStorageCatalog
{
    /// <summary>
    /// Reads a bounded page of journal ids which match <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The journal id prefix, or the default value to list all ids.</param>
    /// <param name="pageSize">The maximum number of storage identities to return.</param>
    /// <param name="continuationToken">An opaque continuation token from a previous page, or <see langword="null"/> to begin a new scan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A bounded page and the token needed to continue the same scan.</returns>
    ValueTask<JournalStorageCatalogPage> ReadPageAsync(
        JournalId prefix,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);
}
