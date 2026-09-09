namespace Orleans.Journaling;

/// <summary>
/// Options for enumerating journal storage identities.
/// </summary>
/// <remarks>
/// Both constraints apply. Providers snapshot these options when enumeration begins.
/// </remarks>
public sealed class ListOptions
{
    /// <summary>
    /// Gets or sets the journal id prefix. The default value matches all ids.
    /// </summary>
    /// <remarks>
    /// A prefix matches the exact journal id and its descendant segments.
    /// </remarks>
    public JournalId Prefix { get; set; }

    /// <summary>
    /// Gets or sets the inclusive upper bound on <see cref="JournalId.Value"/>, compared using
    /// <see cref="StringComparison.Ordinal"/>. The default value does not impose an upper bound.
    /// </summary>
    public JournalId MaxId { get; set; }
}
