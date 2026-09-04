using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Cache contract used by <see cref="RecoverableStreamReceiver{TQueueMessage}"/>.
    /// </summary>
    /// <typeparam name="TQueueMessage">The immutable source record type.</typeparam>
    public interface IRecoverableStreamQueueCache<TQueueMessage> : IQueueCache, IDisposable
    {
        /// <summary>
        /// Packs and adds ordered source records to the cache.
        /// </summary>
        IReadOnlyList<StreamPosition> Add(
            IReadOnlyList<TQueueMessage> messages,
            DateTime dequeueTimeUtc);

        /// <summary>
        /// Tries to get the newest cached provider position.
        /// </summary>
        bool TryGetNewestPosition(
            [NotNullWhen(true)] out StreamSequenceToken? token,
            [NotNullWhen(true)] out string? offset);

        /// <summary>
        /// Tries to get the oldest cached provider position.
        /// </summary>
        bool TryGetOldestPosition(
            [NotNullWhen(true)] out StreamSequenceToken? token,
            [NotNullWhen(true)] out string? offset)
        {
            token = null;
            offset = null;
            return false;
        }

        /// <summary>
        /// Reclaims replay records before or through a partition position.
        /// </summary>
        /// <param name="token">The replay reclamation boundary.</param>
        /// <param name="inclusive">Whether the boundary record is reclaimable.</param>
        /// <param name="utcNow">The current UTC time.</param>
        void UpdateReplayProgress(
            StreamSequenceToken token,
            bool inclusive,
            DateTime utcNow)
            => UpdateDeliveryProgress(inclusive ? token : null, utcNow);

        /// <summary>
        /// Registers a stream whose cursor needs purge metadata while replay is active.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        void RegisterReplayStream(StreamId streamId) { }

        /// <summary>
        /// Unregisters a stream when its last replay cursor leaves the cache.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        void UnregisterReplayStream(StreamId streamId) { }
    }
}
