using System;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Adapts immutable source records to and from pooled cache storage.
    /// </summary>
    /// <typeparam name="TQueueMessage">The source record type.</typeparam>
    public interface IRecoverableStreamDataAdapter<TQueueMessage> : ICacheDataAdapter
    {
        /// <summary>
        /// Gets the stream and provider position for a source record.
        /// </summary>
        StreamPosition GetStreamPosition(TQueueMessage queueMessage);

        /// <summary>
        /// Packs a source record into pooled cache storage.
        /// </summary>
        CachedMessage FromQueueMessage(
            StreamPosition streamPosition,
            TQueueMessage queueMessage,
            DateTime dequeueTimeUtc,
            Func<int, ArraySegment<byte>> getSegment);

        /// <summary>
        /// Gets the provider offset encoded in a cached message.
        /// </summary>
        string GetOffset(ref CachedMessage cachedMessage);

        /// <summary>
        /// Tries to extract a provider offset from a delivery token.
        /// </summary>
        bool TryGetOffset(StreamSequenceToken token, out string offset);

        /// <summary>
        /// Gets the inclusive source-record position represented by a token.
        /// </summary>
        /// <param name="token">The provider token.</param>
        /// <returns>The token normalized to the containing source record.</returns>
        StreamSequenceToken GetRecordToken(StreamSequenceToken token)
            => token is EventSequenceTokenV2 eventToken
                ? eventToken.CreateSequenceTokenForEvent(0)
                : token;
    }
}
