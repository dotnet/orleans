namespace Orleans.Streams;

/// <summary>
/// Tracks the contiguous partition prefix resolved by a cache cursor.
/// </summary>
/// <remarks>
/// A checkpointing cache exposes this capability from each cursor. Scanned records outside
/// the subscription can advance the prefix immediately. Matching records and subsequent
/// scans remain pending until the owner acknowledges the entire selected delivery batch.
/// Cursor selection and materialization failures preserve the position for retry.
/// Implementing this cursor capability does not, by itself, make a cache a checkpointing
/// cache; that capability is advertised by <see cref="ICheckpointingQueueCache"/>.
/// An observed cache miss remains unresolved until the owner deliberately acquires a new cursor.
/// </remarks>
public interface IQueueCacheCursorProgress
{
    /// <summary>
    /// Gets the last fully accounted provider record, or <see langword="null"/> while the prefix is unknown.
    /// </summary>
    /// <remarks>
    /// The value is stable and remains valid after the cursor advances or is disposed.
    /// A token inside a multi-event provider record becomes safe only after the remaining
    /// events have been processed or excluded by the subscription's start position.
    /// </remarks>
    StreamSequenceToken? SafeSequenceToken { get; }

    /// <summary>
    /// Configures a newly acquired cursor to resume after an acknowledged event position.
    /// </summary>
    /// <param name="token">The acknowledged event position.</param>
    /// <remarks>
    /// The cursor accounts for the retained records and any remaining events inside the
    /// boundary record before advancing its safe prefix.
    /// Supplying an acknowledged token does not certify an unknown gap in the cache.
    /// </remarks>
    void SetDeliveredThrough(StreamSequenceToken token);

    /// <summary>
    /// Acknowledges all currently selected matching records and advances the contiguous safe prefix.
    /// </summary>
    /// <remarks>
    /// Call only after all selected delivery and filtering work has completed successfully.
    /// This acknowledgement does not resolve a previously observed cache miss.
    /// </remarks>
    void RecordDeliverySuccess();

    /// <summary>
    /// Rewinds to the first pending matching record while retaining the earlier safe prefix.
    /// </summary>
    /// <remarks>
    /// A transient failure preserves the first pending position for retry. If that position
    /// is unavailable, the cursor retains the loss rather than treating an empty cache as completion.
    /// </remarks>
    /// <exception cref="QueueCacheMissException">
    /// The pending replay range is no longer retained, or the cursor has already observed an unresolved cache miss.
    /// </exception>
    void RecordDeliveryFailure();
}
