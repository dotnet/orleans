
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
        where TQueueMessage : class
        where TCachedMessage : struct
    {
        void QueueMessageToCachedMessage(ref TCachedMessage cachedMessage, TQueueMessage queueMessage);
        IBatchContainer GetBatchContainer(ref TCachedMessage cachedMessage);
        StreamSequenceToken GetSequenceToken(ref TCachedMessage cachedMessage);
        int CompareCachedMessageToSequenceToken(ref TCachedMessage cachedMessage, StreamSequenceToken token);
        bool IsInStream(ref TCachedMessage cachedMessage, Guid streamGuid, string streamNamespace);
        bool ShouldPurge(TCachedMessage cachedMessage, IDisposable purgeRequest);
    }
}
