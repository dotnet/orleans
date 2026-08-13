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
    }
}
