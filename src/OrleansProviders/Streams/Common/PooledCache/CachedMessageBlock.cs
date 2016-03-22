
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
    /// <typeparam name="TCachedMessage">Tightly packed structure.  Struct should contain only value types.</typeparam>
    public class CachedMessageBlock<TCachedMessage> : PooledResource<CachedMessageBlock<TCachedMessage>>
        where TCachedMessage : struct
    {
        private const int OneKb = 1024;
        private const int DefaultCachedMessagesPerBlock = 16 * OneKb; // 16kb

        private readonly TCachedMessage[] cachedMessages;
        private readonly int blockSize;
        private int writeIndex;
        private int readIndex;


        /// <summary>
        /// Linked list node, so this message block can be kept in a linked list
        /// </summary>
        public LinkedListNode<CachedMessageBlock<TCachedMessage>> Node { get; private set; }

        public bool HasCapacity { get { return writeIndex < blockSize; } }

        public bool IsEmpty { get { return readIndex >= writeIndex; } }

        public int NewestMessageIndex { get { return writeIndex - 1; } }
        public int OldestMessageIndex { get { return readIndex; } }

        public TCachedMessage OldestMessage { get { return cachedMessages[OldestMessageIndex]; } }
        public TCachedMessage NewestMessage { get { return cachedMessages[NewestMessageIndex]; } }

        public CachedMessageBlock(IObjectPool<CachedMessageBlock<TCachedMessage>> pool, int blockSize = DefaultCachedMessagesPerBlock)
            : base(pool)
        {
            this.blockSize = blockSize;
            cachedMessages = new TCachedMessage[blockSize];
            writeIndex = 0;
            readIndex = 0;
            Node = new LinkedListNode<CachedMessageBlock<TCachedMessage>>(this);
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

        public void Add<TQueueMessage>(TQueueMessage queueMessage, ICacheDataAdapter<TQueueMessage, TCachedMessage> dataAdapter) where TQueueMessage : class 
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
            dataAdapter.QueueMessageToCachedMessage(ref cachedMessages[index], queueMessage);
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

        public StreamSequenceToken GetSequenceToken<TQueueMessage>(int index, ICacheDataAdapter<TQueueMessage, TCachedMessage> dataAdapter) where TQueueMessage : class
        {
            if (index >= writeIndex || index < readIndex)
            {
                throw new ArgumentOutOfRangeException("index");
            }
            return dataAdapter.GetSequenceToken(ref cachedMessages[index]);
        }

        public StreamSequenceToken GetNewestSequenceToken<TQueueMessage>(ICacheDataAdapter<TQueueMessage, TCachedMessage> dataAdapter) where TQueueMessage : class
        {
            return GetSequenceToken(NewestMessageIndex, dataAdapter);
        }

        public StreamSequenceToken GetOldestSequenceToken<TQueueMessage>(ICacheDataAdapter<TQueueMessage, TCachedMessage> dataAdapter) where TQueueMessage : class
        {
            return GetSequenceToken(OldestMessageIndex, dataAdapter);
        }
        
        public int GetIndexOfFirstMessageLessThanOrEqualTo(StreamSequenceToken token, ICacheDataComparer<TCachedMessage> comparer)
        {
            for (int i = writeIndex - 1; i >= readIndex; i--)
            {
                if (comparer.Compare(cachedMessages[i], token) <= 0)
                {
                    return i;
                }
            }
            throw new ArgumentOutOfRangeException("token");
        }

        public bool TryFindFirstMessage(IStreamIdentity streamIdentity, ICacheDataComparer<TCachedMessage> comparer, out int index)
        {
            return TryFindNextMessage(readIndex, streamIdentity, comparer, out index);
        }

        public bool TryFindNextMessage(int start, IStreamIdentity streamIdentity, ICacheDataComparer<TCachedMessage> comparer, out int index)
        {
            if (start < readIndex)
            {
                throw new ArgumentOutOfRangeException("start");
            }

            for (int i = start; i < writeIndex; i++)
            {
                if (comparer.Compare(cachedMessages[i], streamIdentity) == 0)
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
