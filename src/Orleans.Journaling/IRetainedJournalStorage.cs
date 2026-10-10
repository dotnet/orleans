using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Allows storage to retain independently pinned journal pages.
/// </summary>
/// <remarks>
/// The caller owns the argument and keeps it pinned until the returned operation actually completes,
/// including cancellation or failure. Storage acquires an independent pinned slice before retaining
/// bytes and releases that slice on rejection or when the stored bytes are retired. A failed operation
/// can still have committed: its retained reference then belongs to storage, independently of the
/// caller's completion reference. Providers without this capability use the borrowed sequence contract.
/// </remarks>
internal interface IRetainedJournalStorage
{
    ValueTask AppendRetainedAsync(ArcBuffer value, CancellationToken cancellationToken);

    ValueTask ReplaceRetainedAsync(ArcBuffer value, CancellationToken cancellationToken);
}
