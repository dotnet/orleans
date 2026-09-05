namespace Orleans.Journaling;

/// <summary>
/// Represents one page of journal storage identities.
/// </summary>
public sealed class JournalStorageCatalogPage
{
    /// <summary>
    /// Gets the journal storage identities in this page.
    /// </summary>
    public required IReadOnlyList<JournalId> JournalIds { get; init; }

    /// <summary>
    /// Gets the opaque token used to read the next page, or <see langword="null"/> when the scan is complete.
    /// </summary>
    public string? ContinuationToken { get; init; }
}
