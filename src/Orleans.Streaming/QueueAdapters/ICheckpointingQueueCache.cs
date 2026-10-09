namespace Orleans.Streams;

/// <summary>
/// A partition cache whose checkpoints and eviction are bounded by certified delivery progress.
/// </summary>
/// <remarks>
/// When <see cref="UsesCertifiedDeliveryProgress"/> is true, each successful cursor acquisition
/// supplies an <see cref="IQueueCacheCursorProgress"/>.
/// <see cref="IQueueCache.AddToCache"/> admits a returned read atomically: a recoverable
/// failure preserves the previously admitted prefix so the same read can be retried.
/// The cache owns read recovery, delegating to its receiver where necessary.
/// Progress is published through <see cref="IQueueCache.UpdateDeliveryProgress"/> using
/// a non-null whole-record prefix. Pending or unknown progress withholds the callback.
/// </remarks>
internal interface ICheckpointingQueueCache : IQueueCache, IQueueAdapterReceiverReadRecovery
{
    /// <summary>
    /// Gets whether the initialized provider has selected the certified progress contract.
    /// </summary>
    /// <remarks>The selection remains stable for the receiver lifetime and is independent of transient failures.</remarks>
    bool UsesCertifiedDeliveryProgress => true;
}
