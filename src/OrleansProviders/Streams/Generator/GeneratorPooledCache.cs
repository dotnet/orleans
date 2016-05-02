
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Remoting.Messaging;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    public class GeneratorPooledCache : IQueueCache
    {
        private readonly PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache;

        public GeneratorPooledCache(IObjectPool<FixedSizeBuffer> bufferPool)
        {
            var dataAdapter = new CacheDataAdapter(bufferPool);
            cache = new PooledQueueCache<GeneratedBatchContainer, CachedMessage>(dataAdapter, CacheDataComparer.Instance);
            dataAdapter.PurgeAction = cache.Purge;
        }

        // For fast GC this struct should contain only value types.  I included streamNamespace because I'm lasy and this is test code, but it should not be in here.
        private struct CachedMessage
        {
            public Guid StreamGuid;
            public string StreamNamespace;
            public long SequenceNumber;
            public ArraySegment<byte> Payload;
        }

        private class CacheDataComparer : ICacheDataComparer<CachedMessage>
        {
            public static readonly ICacheDataComparer<CachedMessage> Instance = new CacheDataComparer(); 

            public int Compare(CachedMessage cachedMessage, StreamSequenceToken token)
            {
                var realToken = (EventSequenceToken)token;
                return cachedMessage.SequenceNumber != realToken.SequenceNumber
                    ? (int)(cachedMessage.SequenceNumber - realToken.SequenceNumber)
                    : 0 - realToken.EventIndex;
            }

            public int Compare(CachedMessage cachedMessage, IStreamIdentity streamIdentity)
            {
                int results = cachedMessage.StreamGuid.CompareTo(streamIdentity.Guid);
                return results != 0 ? results : String.Compare(cachedMessage.StreamNamespace, streamIdentity.Namespace, StringComparison.Ordinal);
            }
        }

        private class CacheDataAdapter : ICacheDataAdapter<GeneratedBatchContainer, CachedMessage>
        {
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private FixedSizeBuffer currentBuffer;

            public Action<IDisposable> PurgeAction { private get; set; }

            public CacheDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool)
            {
                if (bufferPool == null)
                {
                    throw new ArgumentNullException("bufferPool");
                }
                this.bufferPool = bufferPool;
            }

            public StreamPosition QueueMessageToCachedMessage(ref CachedMessage cachedMessage, GeneratedBatchContainer queueMessage)
            {
                StreamPosition setreamPosition = GetStreamPosition(queueMessage);
                cachedMessage.StreamGuid = setreamPosition.StreamIdentity.Guid;
                cachedMessage.StreamNamespace = setreamPosition.StreamIdentity.Namespace;
                cachedMessage.SequenceNumber = queueMessage.RealToken.SequenceNumber;
                cachedMessage.Payload = SerializeMessageIntoPooledSegment(queueMessage);
                return setreamPosition;
            }

            // Placed object message payload into a segment from a buffer pool.  When this get's too big, older blocks will be purged
            private ArraySegment<byte> SerializeMessageIntoPooledSegment(GeneratedBatchContainer queueMessage)
            {
                // serialize payload
                byte[] serializedPayload = SerializationManager.SerializeToByteArray(queueMessage.Payload);
                int size = serializedPayload.Length;

                // get segment from current block
                ArraySegment<byte> segment;
                if (currentBuffer == null || !currentBuffer.TryGetSegment(size, out segment))
                {
                    // no block or block full, get new block and try again
                    currentBuffer = bufferPool.Allocate();
                    currentBuffer.SetPurgeAction(PurgeAction);
                    // if this fails with clean block, then requested size is too big
                    if (!currentBuffer.TryGetSegment(size, out segment))
                    {
                        string errmsg = String.Format(CultureInfo.InvariantCulture,
                            "Message size is to big. MessageSize: {0}", size);
                        throw new ArgumentOutOfRangeException("queueMessage", errmsg);
                    }
                }
                Buffer.BlockCopy(serializedPayload, 0, segment.Array, segment.Offset, size);
                return segment;
            }

            public IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
            {
                //Deserialize payload
                var stream = new BinaryTokenStreamReader(cachedMessage.Payload);
                object payloadObject = SerializationManager.Deserialize(stream);
                return new GeneratedBatchContainer(cachedMessage.StreamGuid, cachedMessage.StreamNamespace,
                    payloadObject, new EventSequenceToken(cachedMessage.SequenceNumber));
            }

            public StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage)
            {
                return new EventSequenceToken(cachedMessage.SequenceNumber);
            }

            public StreamPosition GetStreamPosition(GeneratedBatchContainer queueMessage)
            {
                return new StreamPosition(new StreamIdentity(queueMessage.StreamGuid, queueMessage.StreamNamespace), queueMessage.RealToken);
            }

            public bool ShouldPurge(ref CachedMessage cachedMessage, IDisposable purgeRequest)
            {
                var purgedResource = (FixedSizeBuffer) purgeRequest;
                // if we're purging our current buffer, don't use it any more
                if (currentBuffer != null && currentBuffer.Id == purgedResource.Id)
                {
                    currentBuffer = null;
                }
                return cachedMessage.Payload.Array == purgedResource.Id;
            }
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache;
            private readonly object cursor;
            private IBatchContainer current;

            public Cursor(PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache, IStreamIdentity streamIdentity, StreamSequenceToken token)
            {
                this.cache = cache;
                cursor = cache.GetCursor(streamIdentity, token);
            }

            public void Dispose()
            {
            }

            public IBatchContainer GetCurrent(out Exception exception)
            {
                exception = null;
                return current;
            }

            public bool MoveNext()
            {
                IBatchContainer next;
                if (!cache.TryGetNextMessage(cursor, out next))
                {
                    return false;
                }

                current = next;
                return true;
            }

            public void Refresh()
            {
            }

            public void RecordDeliveryFailure()
            {
            }
        }

        public int GetMaxAddCount() { return 100; }

        public void AddToCache(IList<IBatchContainer> messages)
        {
            foreach (IBatchContainer container in messages)
            {
                cache.Add(container as GeneratedBatchContainer);
            }
        }

        public bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;
            return false;
        }

        public IQueueCacheCursor GetCacheCursor(IStreamIdentity streamIdentity, StreamSequenceToken token)
        {
            return new Cursor(cache, streamIdentity, token);
        }

        public bool IsUnderPressure()
        {
            return false;
        }
    }
}
