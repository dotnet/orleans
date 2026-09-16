using System;

namespace Orleans.Streams;

/// <summary>
/// A partition cache whose checkpoints and eviction are bounded by certified delivery progress.
/// </summary>
/// <remarks>
/// Each successful cursor acquisition supplies an <see cref="IQueueCacheCursorProgress"/>.
/// <see cref="IQueueCache.AddToCache"/> admits a returned read atomically: a recoverable
/// failure preserves the previously admitted prefix so the same read can be retried.
/// The associated receiver implements <see cref="IQueueAdapterReceiverReadRecovery"/>.
/// </remarks>
public interface ICheckpointingQueueCache : IQueueCache
{
    /// <summary>
    /// Publishes the last partition record whose subscription and read-accounting obligations are resolved.
    /// </summary>
    /// <param name="safeToken">The certified whole-record prefix. The token is valid only during this call.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <remarks>
    /// The cache applies this certificate to checkpoint updates and eviction during this call.
    /// Pending discovery or unknown progress withholds the call. With no subscriptions, the
    /// pulling agent supplies the last fully accounted read boundary.
    /// </remarks>
    void UpdateDeliveryProgress(StreamSequenceToken safeToken, DateTime utcNow);
}
