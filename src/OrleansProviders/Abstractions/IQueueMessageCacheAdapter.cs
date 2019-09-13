using System;
using Orleans.Streams;

namespace Orleans.Providers.Abstractions
{
    public interface IQueueMessageCacheAdapter
    {
        StreamPosition StreamPosition { get; }
        DateTime EnqueueTimeUtc { get; }
        byte[] OffsetToken { get; }
        int PayloadSize { get; }
        void AppendPayload(ArraySegment<byte> segment);
    }

    public interface IQueueMessageCacheAdapterFactory<TQueueMessage>
    {
        IQueueMessageCacheAdapter Create(string partition, TQueueMessage queueMessage);
    }
}
