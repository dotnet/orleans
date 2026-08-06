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
