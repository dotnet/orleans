
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;
using Tester.TestStreamProviders.Generator.Generators;

namespace Tester.TestStreamProviders.Generator
{
    public class GeneratorPooledCache : IQueueCache
    {
        private readonly PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache;

        public GeneratorPooledCache(IObjectPool<FixedSizeBuffer> bufferPool)
        {
            cache = new PooledQueueCache<GeneratedBatchContainer, CachedMessage>(new CacheDataAdapter(bufferPool, Purge));
        }

        // For fast GC this struct should contain only value types.  I included streamNamespace because I'm lasy and this is test code, but it should not be in here.
        private struct CachedMessage
        {
            public Guid StreamGuid;
            public string StreamNamespace;
            public long SequenceNumber;
            public ArraySegment<byte> Payload;
        }

        private class CacheDataAdapter : ICacheDataAdapter<GeneratedBatchContainer, CachedMessage>
        {
            private readonly IObjectPool<FixedSizeBuffer> bufferPool;
            private readonly Action<IDisposable> purgeAction;
            private FixedSizeBuffer currentBuffer;

            public CacheDataAdapter(IObjectPool<FixedSizeBuffer> bufferPool, Action<IDisposable> purgeAction)
            {
                if (bufferPool == null)
                {
                    throw new ArgumentNullException("bufferPool");
                }
                if (purgeAction == null)
                {
                    throw new ArgumentNullException("purgeAction");
                }
                this.bufferPool = bufferPool;
                this.purgeAction = purgeAction;
            }

            public void QueueMessageToCachedMessage(ref CachedMessage cachedMessage, GeneratedBatchContainer queueMessage)
            {
                cachedMessage.StreamGuid = queueMessage.StreamGuid;
                cachedMessage.StreamNamespace = queueMessage.StreamNamespace;
                cachedMessage.SequenceNumber = queueMessage.RealToken.SequenceNumber;
                cachedMessage.Payload = SerializeMessageIntoPooledSegment(queueMessage);
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
                    currentBuffer.SetPurgeAction(purgeAction);
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

            public int CompareCachedMessageToSequenceToken(ref CachedMessage cachedMessage, StreamSequenceToken token)
            {
                var realToken = (EventSequenceToken) token;
                return cachedMessage.SequenceNumber != realToken.SequenceNumber
                    ? (int) (cachedMessage.SequenceNumber - realToken.SequenceNumber)
                    : 0 - realToken.EventIndex;
            }

            public bool IsInStream(ref CachedMessage cachedMessage, Guid streamGuid, string streamNamespace)
            {
                return cachedMessage.StreamGuid == streamGuid && cachedMessage.StreamNamespace == streamNamespace;
            }

            public bool ShouldPurge(CachedMessage cachedMessage, IDisposable purgeRequest)
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

            public Cursor(PooledQueueCache<GeneratedBatchContainer, CachedMessage> cache, Guid streamGuid, string streamNamespace, StreamSequenceToken token)
            {
                this.cache = cache;
                cursor = cache.GetCursor(streamGuid, streamNamespace, token);
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
        }

        public int MaxAddCount { get { return 100; } }

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

        public IQueueCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token)
        {
            return new Cursor(cache, streamGuid, streamNamespace, token);
        }

        public bool IsUnderPressure()
        {
            return false;
        }

        private void Purge(IDisposable disposable)
        {
            cache.Purge(disposable);
        }
    }
}
