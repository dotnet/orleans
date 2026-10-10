using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Internal opt-in capability for retaining serialized journal pages without copying them.
/// </summary>
/// <remarks>
/// The caller owns the argument and keeps it pinned until the returned operation actually completes,
/// including cancellation or failure. Implementations must acquire an independent pinned slice before
/// retaining any bytes, and release it on rejection or when the stored bytes are retired. Copying an
/// ArcBuffer value is not an ownership transfer. A failed/ambiguous operation can still have committed:
/// its retained reference then belongs to storage, independently of the caller's completion reference.
/// This capability does not change IJournalStorage's borrowed ReadOnlySequence contract.
/// </remarks>
internal interface IRetainedJournalStorage
{
    ValueTask AppendRetainedAsync(ArcBuffer value, CancellationToken cancellationToken);
    ValueTask ReplaceRetainedAsync(ArcBuffer value, CancellationToken cancellationToken);
}
