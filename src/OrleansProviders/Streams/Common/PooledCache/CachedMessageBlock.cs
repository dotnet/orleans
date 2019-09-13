
using System;

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

        public CachedMessageBlock Previous { get; set; }
        public CachedMessageBlock Next { get; set; }

        /// <summary>
        /// More messages can be added to the blocks
        /// </summary>
        public bool HasCapacity => this.writeIndex < this.blockSize;

        /// <summary>
        /// Block is empty
        /// </summary>
        public bool IsEmpty => this.OldestMessageIndex >= this.writeIndex;

        /// <summary>
        /// Index of most recent message added to the block
        /// </summary>
        public int NewestMessageIndex => this.writeIndex - 1;

        /// <summary>
        /// Index of oldest message in this block
        /// </summary>
        public int OldestMessageIndex { get; private set; }

        /// <summary>
        /// Oldest message in the block
        /// </summary>
        public CachedMessage OldestMessage => this.cachedMessages[this.OldestMessageIndex];

        /// <summary>
        /// Newest message in this block
        /// </summary>
        public CachedMessage NewestMessage => this.cachedMessages[this.NewestMessageIndex];

        /// <summary>
        /// Message count in this block
        /// </summary>
        public int ItemCount => Math.Max(0, this.writeIndex - this.OldestMessageIndex);

        /// <summary>
        /// Block of cached messages
        /// </summary>
        /// <param name="blockSize"></param>
        public CachedMessageBlock(int blockSize = DefaultCachedMessagesPerBlock)
        {
            this.blockSize = blockSize;
            this.cachedMessages = new CachedMessage[blockSize];
            this.writeIndex = 0;
            this.OldestMessageIndex = 0;
        }

        /// <summary>
        /// Removes a message from the start of the block (oldest data).  Returns true if more items are still available.
        /// </summary>
        /// <returns></returns>
        public bool Remove()
        {
            if (this.OldestMessageIndex < this.writeIndex)
            {
                this.OldestMessageIndex++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Add a message from the queue to the block.
        /// Converts the queue message to a cached message and stores it at the end of the block.
        /// </summary>
        public void Add(in CachedMessage message)
        {
            if (!this.HasCapacity)
            {
                throw new InvalidOperationException("Block is full");
            }

            int index = this.writeIndex++;
            this.cachedMessages[index] = message;
        }

        /// <summary>
        /// Access the cached message at the provided index.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public ref CachedMessage this[int index] =>  ref this.cachedMessages[index];

        /// <summary>
        /// Gets the sequence token of the cached message a the provided index
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public ArraySegment<byte> GetSequenceToken(int index)
        {
            if (index >= this.writeIndex || index < this.OldestMessageIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            ref var msg = ref this.cachedMessages[index];
            return msg.SequenceToken();
        }

        /// <summary>
        /// Gets the sequence token of the newest message in this block
        /// </summary>
        /// <returns></returns>
        public ArraySegment<byte> GetNewestSequenceToken() => this.GetSequenceToken(this.NewestMessageIndex);

        /// <summary>
        /// Gets the sequence token of the oldest message in this block
        /// </summary>
        /// <returns></returns>
        public ArraySegment<byte> GetOldestSequenceToken() => this.GetSequenceToken(this.OldestMessageIndex);

        /// <summary>
        /// Gets the index of the first message in this block that has a sequence token at or before the provided token
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public int GetIndexOfFirstMessageLessThanOrEqualTo(in ReadOnlySpan<byte> token)
        {
            for (int i = this.writeIndex - 1; i >= this.OldestMessageIndex; i--)
            {
                if (this.cachedMessages[i].Compare(token) <= 0)
                {
                    return i;
                }
            }
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        /// <summary>
        /// Tries to find the first message in the block that is part of the provided stream.
        /// </summary>
        public bool TryFindFirstMessage(byte[] streamIdentity, out int index)
            => this.TryFindNextMessage(this.OldestMessageIndex, streamIdentity, out index);

        /// <summary>
        /// Tries to get the next message from the provided stream, starting at the start index.
        /// </summary>
        public bool TryFindNextMessage(int start, byte[] streamIdentity, out int index)
        {
            if (start < this.OldestMessageIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(start));
            }

            for (int i = start; i < this.writeIndex; i++)
            {
                if (this.cachedMessages[i].CompareStreamId(streamIdentity))
                {
                    index = i;
                    return true;
                }
            }

            index = this.writeIndex - 1;
            return false;
        }

        /// <summary>
        /// Resets this blocks state to that of an empty block.
        /// </summary>
        public override void OnResetState()
        {
            this.writeIndex = 0;
            this.OldestMessageIndex = 0;
            this.Next = null;
            this.Previous = null;
        }
    }
}
