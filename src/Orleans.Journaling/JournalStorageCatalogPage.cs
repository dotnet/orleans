namespace Orleans.Journaling;

/// <summary>
/// Represents one page of journal storage identities in provider traversal order.
/// </summary>
public sealed class JournalStorageCatalogPage
{
    /// <summary>
    /// Gets the matching journal ids. An empty page can have a continuation token.
    /// </summary>
    public required IReadOnlyList<JournalId> JournalIds { get; init; }

    /// <summary>
    /// Gets the opaque token for the next page, or <see langword="null"/> when the traversal is complete.
    /// </summary>
    public string? ContinuationToken { get; init; }
}
