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
    /// Lists journal ids which match <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The journal id prefix, or the default value to list all ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Matching ids in lexicographic <see cref="JournalId.Value"/> order.</returns>
    IAsyncEnumerable<JournalId> ListAsync(JournalId prefix = default, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides optional, resumable catalog paging for journal storage instances.
/// </summary>
/// <remarks>
/// Pages follow provider traversal order. An unchanged catalog can be traversed by passing each returned
/// continuation token to the next call until the token is <see langword="null"/>. Providers describe their
/// traversal and consistency guarantees; callers should tolerate repeated identities during concurrent changes.
/// Each page reflects storage observed during that request, so concurrent creation and deletion can affect
/// which identities a traversal observes. A new traversal observes subsequent catalog changes.
/// </remarks>
public interface IPagedJournalStorageCatalog
{
    /// <summary>
    /// Reads a page of journal ids which match <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The journal id prefix, or the default value to list all ids.</param>
    /// <param name="pageSize">
    /// The positive maximum number of journal ids to return. Providers document the storage records examined
    /// per page and the memory and work required to produce it.
    /// </param>
    /// <param name="continuationToken">
    /// An opaque token from the preceding page, or <see langword="null"/> to start a traversal.
    /// Use the token with the same prefix and provider instance during its initialized lifetime.
    /// The page size can change between calls. Storage services can expire their continuation tokens.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// At most <paramref name="pageSize"/> matching ids and a continuation token.
    /// An empty page with a non-null token advances the traversal; continue using that token.
    /// A null token indicates completion.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is zero or negative.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="continuationToken"/> is malformed or belongs to another prefix or provider instance.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<JournalStorageCatalogPage> ReadPageAsync(
        JournalId prefix,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);
}
