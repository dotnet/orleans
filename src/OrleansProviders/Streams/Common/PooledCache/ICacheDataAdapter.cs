
using System;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pooled queue cache stores data in tightly packed structures that need to be transformed to various
    ///   other formats quickly.  Since the data formats may change by queue type and data format,
    ///   this interface allows adapter developers to build custom data transforms appropriate for 
    ///   the various types of queue data.
    /// </summary>
    /// <typeparam name="TQueueMessage"></typeparam>
    /// <typeparam name="TCachedMessage"></typeparam>
    public interface ICacheDataAdapter<in TQueueMessage, TCachedMessage>
        where TCachedMessage : struct
    {
        /// <summary>
        /// Converts a TQueueMessage message from the queue to a TCachedMessage cachable structures.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="queueMessage"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        StreamPosition QueueMessageToCachedMessage(ref TCachedMessage cachedMessage, TQueueMessage queueMessage, DateTime dequeueTimeUtc);

        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        IBatchContainer GetBatchContainer(ref TCachedMessage cachedMessage);

        /// <summary>
        /// Gets the stream sequence token from a cached message.
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <returns></returns>
        StreamSequenceToken GetSequenceToken(ref TCachedMessage cachedMessage);

        /// <summary>
        /// Gets the stream position from a queue message
        /// </summary>
        /// <param name="queueMessage"></param>
        /// <returns></returns>
        StreamPosition GetStreamPosition(TQueueMessage queueMessage);

        /// <summary>
        /// Should be set to OnBlockAllocated method of the cache's EvicationStrategy
        /// </summary>
        Action<FixedSizeBuffer> OnBlockAllocated { set; }

        /// <summary>
        /// Get cached message enqueueTime
        /// </summary>
        /// <param name="message"></param>
        /// <returns>Return message's enqueue time Utc, return null if message didn't keep a enqueue time</returns>
        DateTime? GetMessageEnqueueTimeUtc(ref TCachedMessage message);

        /// <summary>
        /// Get cached message dequeue time
        /// </summary>
        /// <param name="message"></param>
        /// <returns>Return message's dequeue time Utc, return null if the message didn't keep a dequeue time</returns>
        DateTime? GetMessageDequeueTimeUtc(ref TCachedMessage message);
    }

    /// <summary>
    /// Compares cached messages with various stream details
    /// </summary>
    /// <typeparam name="TCachedMessage"></typeparam>
    public interface ICacheDataComparer<in TCachedMessage>
    {
        /// <summary>
        /// Compare a cached message with a sequence token to determine if it message is before or after the token
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="streamToken"></param>
        /// <returns></returns>
        int Compare(TCachedMessage cachedMessage, StreamSequenceToken streamToken);

        /// <summary>
        /// Checks to see if the cached message is part of the provided stream
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="streamIdentity"></param>
        /// <returns></returns>
        bool Equals(TCachedMessage cachedMessage, IStreamIdentity streamIdentity);
    }

    /// <summary>
    /// Cache data comparer exstension functions that switch argument order
    /// </summary>
    public static class CacheDataComparerExtensions
    {
        /// <summary>
        /// Compare a cached message with a sequence token to determine if it message is before or after the token
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="comparer"></param>
        /// <param name="streamToken"></param>
        /// <returns></returns>
        public static int Compare<TCachedMessage>(this ICacheDataComparer<TCachedMessage> comparer, StreamSequenceToken streamToken, TCachedMessage cachedMessage)
        {
            return 0 - comparer.Compare(cachedMessage, streamToken);
        }

        /// <summary>
        /// Checks to see if the cached message is part of the provided stream
        /// </summary>
        /// <param name="cachedMessage"></param>
        /// <param name="comparer"></param>
        /// <param name="streamIdentity"></param>
        /// <returns></returns>
        public static bool Equals<TCachedMessage>(this ICacheDataComparer<TCachedMessage> comparer, IStreamIdentity streamIdentity, TCachedMessage cachedMessage)
        {
            return comparer.Equals(cachedMessage, streamIdentity);
        }
    }
}
