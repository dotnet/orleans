namespace Orleans.Journaling;

/// <summary>
/// A journal identity and an optional metadata snapshot returned by a catalog.
/// </summary>
/// <param name="Id">The journal identity.</param>
/// <param name="Metadata">
/// The journal format, storage ETag, and complete caller-owned metadata properties observed together,
/// or <see langword="null"/> when metadata was not requested or is unavailable from the listing.
/// </param>
/// <remarks>
/// Metadata describes the journal at the time it was listed. Use its ETag for conditional updates;
/// concurrent changes can invalidate the snapshot. A non-null snapshot has the same metadata semantics
/// as <see cref="IJournalStorage.GetMetadataAsync"/>.
/// </remarks>
public readonly record struct JournalCatalogEntry(JournalId Id, IJournalMetadata? Metadata = null);
