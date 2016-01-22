
using System;
using System.Collections.Generic;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// CachedMessageBlock is a block of tightly packed structures containing tracking data for cached messages.  This data is 
    ///   tightly packed to reduced GC pressure.  The tracking data is used by the queue cache to walk the cache serving ordered
    ///   queue messages by stream.
    /// </summary>
    /// <typeparam name="TQueueMessage"></typeparam>
    /// <typeparam name="TCachedMessage">Tightly packed structure.  Struct should contain only value types.</typeparam>
    public class CachedMessageBlock<TQueueMessage, TCachedMessage> : PooledResource<CachedMessageBlock<TQueueMessage, TCachedMessage>>
        where TQueueMessage : class
        where TCachedMessage : struct
    {
        private const int OneKb = 1024;
        private const int DefaultCachedMessagesPerBlock = 16 * OneKb; // 16kb

        private readonly TCachedMessage[] cachedMessages;
        private readonly int blockSize;
        private int writeIndex;
        private int readIndex;
        private readonly ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter;


        /// <summary>
        /// Linked list node, so this message block can be kept in a linked list
        /// </summary>
        public LinkedListNode<CachedMessageBlock<TQueueMessage, TCachedMessage>> Node { get; private set; }

        public bool HasCapacity { get { return writeIndex < blockSize; } }

        public bool IsEmpty { get { return readIndex >= writeIndex; } }

        public int NewestMessageIndex { get { return writeIndex - 1; } }
        public int OldestMessageIndex { get { return readIndex; } }

        public TCachedMessage OldestMessage { get { return this[OldestMessageIndex]; } }
        public TCachedMessage NewestMessage { get { return this[NewestMessageIndex]; } }

        public StreamSequenceToken OldestSequenceToken { get { return GetSequenceToken(OldestMessageIndex); } }
        public StreamSequenceToken NewestSequenceToken { get { return GetSequenceToken(NewestMessageIndex); } }

        public CachedMessageBlock(IObjectPool<CachedMessageBlock<TQueueMessage, TCachedMessage>> pool, ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter, int blockSize = DefaultCachedMessagesPerBlock)
            : base(pool)
        {
            if (cacheDataAdapter == null)
            {
                throw new ArgumentNullException("cacheDataAdapter");
            }
            this.cacheDataAdapter = cacheDataAdapter;
            this.blockSize = blockSize;
            cachedMessages = new TCachedMessage[blockSize];
            writeIndex = 0;
            readIndex = 0;
            Node = new LinkedListNode<CachedMessageBlock<TQueueMessage, TCachedMessage>>(this);
        }

        /// <summary>
        /// Removes a message from the start of the block (oldest data).  Returns true if more items are still available.
        /// </summary>
        /// <returns></returns>
        public bool Remove()
        {
            if (readIndex < writeIndex)
            {
                readIndex++;
                return true;
            }
            return false;
        }

        public void Add(TQueueMessage queueMessage)
        {
            if (queueMessage == null)
            {
                throw new ArgumentNullException("queueMessage");
            }
            if (!HasCapacity)
            {
                throw new InvalidOperationException("Block is full");
            }

            int index = writeIndex++;
            cacheDataAdapter.QueueMessageToCachedMessage(ref cachedMessages[index], queueMessage);
        }

        public TCachedMessage this[int index]
        {
            get
            {
                if (index >= writeIndex || index < readIndex)
                {
                    throw new ArgumentOutOfRangeException("index");
                }
                return cachedMessages[index];
            }
        }

        public StreamSequenceToken GetSequenceToken(int index)
        {
            if (index >= writeIndex || index < readIndex)
            {
                throw new ArgumentOutOfRangeException("index");
            }
            return cacheDataAdapter.GetSequenceToken(ref cachedMessages[index]);
        }

        public int GetIndexOfFirstMessageLessThanOrEqualTo(StreamSequenceToken token)
        {
            for (int i = writeIndex - 1; i >= readIndex; i--)
            {
                if (cacheDataAdapter.CompareCachedMessageToSequenceToken(ref cachedMessages[i], token) <= 0)
                {
                    return i;
                }
            }
            throw new ArgumentOutOfRangeException("token");
        }

        public bool TryFindFirstMessage(Guid streamGuid, string streamNamespace, out int index)
        {
            return TryFindNextMessage(readIndex, streamGuid, streamNamespace, out index);
        }

        public bool TryFindNextMessage(int start, Guid streamGuid, string streamNamespace, out int index)
        {
            if (start < readIndex)
            {
                throw new ArgumentOutOfRangeException("start");
            }

            for (int i = start; i < writeIndex; i++)
            {
                if (cacheDataAdapter.IsInStream(ref cachedMessages[i], streamGuid, streamNamespace))
                {
                    index = i;
                    return true;
                }
            }

            index = writeIndex - 1;
            return false;
        }

        public override void OnResetState()
        {
            writeIndex = 0;
            readIndex = 0;
        }
    }
}
