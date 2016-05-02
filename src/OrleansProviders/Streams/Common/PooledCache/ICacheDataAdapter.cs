
using System;
using Orleans.Runtime;
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
        where TQueueMessage : class
        where TCachedMessage : struct
    {
        StreamPosition QueueMessageToCachedMessage(ref TCachedMessage cachedMessage, TQueueMessage queueMessage);
        IBatchContainer GetBatchContainer(ref TCachedMessage cachedMessage);
        StreamSequenceToken GetSequenceToken(ref TCachedMessage cachedMessage);
        StreamPosition GetStreamPosition(TQueueMessage queueMessage);
        bool ShouldPurge(ref TCachedMessage cachedMessage, IDisposable purgeRequest);
        Action<IDisposable> PurgeAction { set; }
    }

    public interface ICacheDataComparer<in TCachedMessage>
    {
        int Compare(TCachedMessage cachedMessage, StreamSequenceToken streamToken);
        int Compare(TCachedMessage cachedMessage, IStreamIdentity streamIdentity);
    }

    public static class CacheDataComparerExtensions
    {
        public static int Compare<TCachedMessage>(this ICacheDataComparer<TCachedMessage> comparer, StreamSequenceToken streamToken, TCachedMessage cachedMessage)
        {
            return 0 - comparer.Compare(cachedMessage, streamToken);
        }

        public static int Compare<TCachedMessage>(this ICacheDataComparer<TCachedMessage> comparer, IStreamIdentity streamIdentity, TCachedMessage cachedMessage)
        {
            return 0 - comparer.Compare(cachedMessage, streamIdentity);
        }
    }
}
