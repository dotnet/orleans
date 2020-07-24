
using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// CachedMessageBlock is a block of tightly packed structures containing tracking data for cached messages.  This data is 
    ///   tightly packed to reduced GC pressure.  The tracking data is used by the queue cache to walk the cache serving ordered
    ///   queue messages by stream.
    /// </summary>
    public class CachedMessageBlock : PooledResource<CachedMessageBlock>
    {
        private const int OneKb = 1024;
        private const int DefaultCachedMessagesPerBlock = 16 * OneKb; // 16kb

        private readonly CachedMessage[] cachedMessages;
        private readonly int blockSize;
        private int writeIndex;
        private int readIndex;

        /// <summary>
        /// Linked list node, so this message block can be kept in a linked list
        /// </summary>
        public LinkedListNode<CachedMessageBlock> Node { get; private set; }

        /// <summary>
        /// More messages can be added to the blocks
        /// </summary>
        public bool HasCapacity => writeIndex < blockSize;

        /// <summary>
        /// Block is empty
        /// </summary>
        public bool IsEmpty => readIndex >= writeIndex;

        /// <summary>
        /// Index of most recent message added to the block
        /// </summary>
        public int NewestMessageIndex => writeIndex - 1;

        /// <summary>
        /// Index of oldest message in this block
        /// </summary>
        public int OldestMessageIndex => readIndex;

        /// <summary>
        /// Oldest message in the block
        /// </summary>
        public CachedMessage OldestMessage => cachedMessages[OldestMessageIndex];

        /// <summary>
        /// Newest message in this block
        /// </summary>
        public CachedMessage NewestMessage => cachedMessages[NewestMessageIndex];

        /// <summary>
        /// Message count in this block
        /// </summary>
        public int ItemCount { get
            {
                int count = writeIndex - readIndex;
                return count >= 0 ? count : 0;
            }  
        }

        /// <summary>
        /// Block of cached messages
        /// </summary>
        /// <param name="blockSize"></param>
        public CachedMessageBlock(int blockSize = DefaultCachedMessagesPerBlock)
        {
            this.blockSize = blockSize;
            cachedMessages = new CachedMessage[blockSize];
            writeIndex = 0;
            readIndex = 0;
            Node = new LinkedListNode<CachedMessageBlock>(this);
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

        /// <summary>
        /// Add a message from the queue to the block.
        /// Converts the queue message to a cached message and stores it at the end of the block.
        /// </summary>
        public void Add(CachedMessage message)
        {
            if (!HasCapacity)
            {
                throw new InvalidOperationException("Block is full");
            }

            int index = writeIndex++;
            cachedMessages[index] = message;
        }

        /// <summary>
        /// Access the cached message at the provided index.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public CachedMessage this[int index]
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

        /// <summary>
        /// Gets the sequence token of the cached message a the provided index
        /// </summary>
        /// <param name="index"></param>
        /// <param name="dataAdapter"></param>
        /// <returns></returns>
        public StreamSequenceToken GetSequenceToken(int index, ICacheDataAdapter dataAdapter)
        {
            if (index >= writeIndex || index < readIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return dataAdapter.GetSequenceToken(ref cachedMessages[index]);
        }

        /// <summary>
        /// Gets the sequence token of the newest message in this block
        /// </summary>
        /// <param name="dataAdapter"></param>
        /// <returns></returns>
        public StreamSequenceToken GetNewestSequenceToken(ICacheDataAdapter dataAdapter)
        {
            return GetSequenceToken(NewestMessageIndex, dataAdapter);
        }

        /// <summary>
        /// Gets the sequence token of the oldest message in this block
        /// </summary>
        /// <param name="dataAdapter"></param>
        /// <returns></returns>
        public StreamSequenceToken GetOldestSequenceToken(ICacheDataAdapter dataAdapter)
        {
            return GetSequenceToken(OldestMessageIndex, dataAdapter);
        }
        
        /// <summary>
        /// Gets the index of the first message in this block that has a sequence token at or before the provided token
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public int GetIndexOfFirstMessageLessThanOrEqualTo(StreamSequenceToken token)
        {
            for (int i = writeIndex - 1; i >= readIndex; i--)
            {
                if (cachedMessages[i].Compare(token) <= 0)
                {
                    return i;
                }
            }
            throw new ArgumentOutOfRangeException("token");
        }

        /// <summary>
        /// Tries to find the first message in the block that is part of the provided stream.
        /// </summary>
        public bool TryFindFirstMessage(StreamId streamId, ICacheDataAdapter dataAdapter, out int index)
        {
            return TryFindNextMessage(readIndex, streamId, dataAdapter, out index);
        }

        /// <summary>
        /// Tries to get the next message from the provided stream, starting at the start index.
        /// </summary>
        public bool TryFindNextMessage(int start, StreamId streamId, ICacheDataAdapter dataAdapter, out int index)
        {
            if (start < readIndex)
            {
                throw new ArgumentOutOfRangeException("start");
            }

            for (int i = start; i < writeIndex; i++)
            {
                if (cachedMessages[i].CompareStreamId(streamId))
                {
                    index = i;
                    return true;
                }
            }

            index = writeIndex - 1;
            return false;
        }

        /// <summary>
        /// Resets this blocks state to that of an empty block.
        /// </summary>
        public override void OnResetState()
        {
            writeIndex = 0;
            readIndex = 0;
        }
    }
}
