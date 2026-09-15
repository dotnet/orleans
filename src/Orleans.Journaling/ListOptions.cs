namespace Orleans.Journaling;

/// <summary>
/// Options for enumerating journal storage identities.
/// </summary>
/// <remarks>
/// All constraints apply using ordinal comparisons. Providers snapshot these options when enumeration begins.
/// </remarks>
public sealed class ListOptions
{
    /// <summary>
    /// Gets or sets the raw prefix of <see cref="JournalId.Value"/>. The default value matches all ids.
    /// </summary>
    /// <remarks>
    /// Prefixes can end within a path segment, for example <c>jobs/shards/20260909</c>.
    /// Include a trailing slash to select descendants of a namespace.
    /// </remarks>
    public JournalId Prefix { get; set; }

    /// <summary>
    /// Gets or sets the inclusive lower bound on <see cref="JournalId.Value"/>, compared using
    /// <see cref="StringComparison.Ordinal"/>. The default value does not impose a lower bound.
    /// </summary>
    public JournalId MinId { get; set; }

    /// <summary>
    /// Gets or sets the inclusive upper bound on <see cref="JournalId.Value"/>, compared using
    /// <see cref="StringComparison.Ordinal"/>. The default value does not impose an upper bound.
    /// </summary>
    public JournalId MaxId { get; set; }

    /// <summary>
    /// Gets or sets whether the catalog includes metadata available from its listing operation.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Providers project a complete metadata snapshot when available without a separate per-journal
    /// metadata request. Otherwise, <see cref="JournalCatalogEntry.Metadata"/> is <see langword="null"/>.
    /// Callers which require missing metadata can retrieve it using <see cref="IJournalStorage.GetMetadataAsync"/>.
    /// </remarks>
    public bool IncludeMetadata { get; set; }
}
