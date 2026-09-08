namespace Orleans.Journaling;

/// <summary>
/// Options for enumerating journal storage identities.
/// </summary>
public sealed class JournalStorageCatalogOptions
{
    /// <summary>
    /// Gets or sets the journal id prefix. The default value matches all ids.
    /// </summary>
    /// <remarks>
    /// A prefix matches the exact journal id and its descendant segments.
    /// </remarks>
    public JournalId Prefix { get; set; }
}
