
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Orleans.Streams;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// The PooledQueueCache is a cache that is intended to serve as a message cache in an IQueueCache.
    /// It is capable of storing large numbers of messages (gigs worth of messages) for extended periods
    ///   of time (minutes to indefinite), while incurring a minimal performance hit due to garbage collection.
    /// This pooled cache allocates memory and never releases it. It keeps freed resources available in pools 
    ///   that remain in application use through the life of the service. This means these objects go to gen2,
    ///   are compacted, and then stay there. This is relatively cheap, as the only cost they now incur is
    ///   the cost of checking to see if they should be freed in each collection cycle. Since this cache uses
    ///   small numbers of large objects with relatively simple object graphs, they are less costly to check
    ///   then large numbers of smaller objects with more complex object graphs.
    /// For performance reasons this cache is designed to more closely align with queue specific data.  This is,
    ///   in part, why, unlike the SimpleQueueCache, this cache does not implement IQueueCache.  It is intended
    ///   to be used in queue specific implementations of IQueueCache.
    /// </summary>
    /// <typeparam name="TQueueMessage">Queue specific data</typeparam>
    /// <typeparam name="TCachedMessage">Tightly packed cached structure.  Should only contain value types.</typeparam>
    public class PooledQueueCache<TQueueMessage, TCachedMessage>: IPurgeObservable<TCachedMessage>
        where TCachedMessage : struct
    {
        // linked list of message bocks.  First is newest.
        private readonly LinkedList<CachedMessageBlock<TCachedMessage>> messageBlocks;
        private readonly CachedMessagePool<TQueueMessage, TCachedMessage> pool;
        private readonly ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter;
        private readonly ICacheDataComparer<TCachedMessage> comparer;
        private readonly Logger logger;
        private readonly ICacheMonitor cacheMonitor;
        private int itemCount;
        private Timer timer;
        /// <summary>
        /// Cached message most recently added
        /// </summary>
        public TCachedMessage? Newest
        {
            get
            {
                if (IsEmpty)
                    return null;
                return messageBlocks.First.Value.NewestMessage;
            }
        }

        /// <summary>
        /// Oldest message in cache
        /// </summary>
        public TCachedMessage? Oldest
        {
            get
            {
                if (IsEmpty)
                    return null;
                return messageBlocks.Last.Value.OldestMessage;
            }
        }

        /// <summary>
        /// Cached message count
        /// </summary>
        public int ItemCount { get { return this.itemCount; }
        }

        /// <summary>
        /// Pooled queue cache is a cache of message that obtains resource from a pool
        /// </summary>
        /// <param name="cacheDataAdapter"></param>
        /// <param name="comparer"></param>
        /// <param name="logger"></param>
        /// <param name="cacheMonitor"></param>
        /// <param name="cacheMonitorWriteInterval">cache monitor write interval</param>
        public PooledQueueCache(ICacheDataAdapter<TQueueMessage, TCachedMessage> cacheDataAdapter, ICacheDataComparer<TCachedMessage> comparer, Logger logger, ICacheMonitor cacheMonitor, TimeSpan? cacheMonitorWriteInterval)
        {
            if (cacheDataAdapter == null)
            {
                throw new ArgumentNullException("cacheDataAdapter");
            }
            if (comparer == null)
            {
                throw new ArgumentNullException("comparer");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            this.cacheDataAdapter = cacheDataAdapter;
            this.comparer = comparer;
            this.logger = logger.GetSubLogger("messagecache", "-");
            this.itemCount = 0;
            pool = new CachedMessagePool<TQueueMessage, TCachedMessage>(cacheDataAdapter);
            messageBlocks = new LinkedList<CachedMessageBlock<TCachedMessage>>();
            this.cacheMonitor = cacheMonitor;

            if (this.cacheMonitor != null && cacheMonitorWriteInterval.HasValue)
            {
                this.timer = new Timer(this.ReportCacheMessageStatistics, null, cacheMonitorWriteInterval.Value, cacheMonitorWriteInterval.Value);
            }
           
        }

        /// <summary>
        /// Indicates whether the cach is empty
        /// </summary>
        public bool IsEmpty => messageBlocks.Count == 0 || (messageBlocks.Count == 1 && messageBlocks.First.Value.IsEmpty);

        /// <summary>
        /// Acquires a cursor to enumerate through the messages in the cache at the provided sequenceToken, 
        ///   filtered on the specified stream.
        /// </summary>
        /// <param name="streamIdentity">stream identity</param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(IStreamIdentity streamIdentity, StreamSequenceToken sequenceToken)
        {
            var cursor = new Cursor(streamIdentity);
            SetCursor(cursor, sequenceToken);
            return cursor;
        }

        private void ReportCacheMessageStatistics(object state)
        {
            if (this.IsEmpty)
            {
                this.cacheMonitor.ReportMessageStatistics(null, null, null, this.ItemCount);
            }
            else
            {
                var newestMessage = this.Newest.Value;
                var oldestMessage = this.Oldest.Value;
                var now = DateTime.UtcNow;
                var newestMessageEnqueueTime = this.cacheDataAdapter.GetMessageEnqueueTimeUtc(ref newestMessage);
                var oldestMessageEnqueueTime = this.cacheDataAdapter.GetMessageEnqueueTimeUtc(ref oldestMessage);
                var oldestMessageDequeueTime = this.cacheDataAdapter.GetMessageDequeueTimeUtc(ref oldestMessage);
                this.cacheMonitor.ReportMessageStatistics(oldestMessageEnqueueTime, oldestMessageDequeueTime, newestMessageEnqueueTime, this.itemCount);
            }
        }

        private void SetCursor(Cursor cursor, StreamSequenceToken sequenceToken)
        {
            // If nothing in cache, unset token, and wait for more data.
            if (messageBlocks.Count == 0)
            {
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            LinkedListNode<CachedMessageBlock<TCachedMessage>> newestBlock = messageBlocks.First;

            // if sequenceToken is null, iterate from newest message in cache
            if (sequenceToken == null)
            {
                cursor.State = CursorStates.Idle;
                cursor.CurrentBlock = newestBlock;
                cursor.Index = newestBlock.Value.NewestMessageIndex;
                cursor.SequenceToken = newestBlock.Value.GetNewestSequenceToken(cacheDataAdapter);
                return;
            }

            // If sequenceToken is too new to be in cache, unset token, and wait for more data.
            TCachedMessage newestMessage = newestBlock.Value.NewestMessage;
            if (comparer.Compare(newestMessage, sequenceToken) < 0) 
            {
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            // Check to see if sequenceToken is too old to be in cache
            TCachedMessage oldestMessage = messageBlocks.Last.Value.OldestMessage;
            if (comparer.Compare(oldestMessage, sequenceToken) > 0)
            {
                // throw cache miss exception
                throw new QueueCacheMissException(sequenceToken,
                    messageBlocks.Last.Value.GetOldestSequenceToken(cacheDataAdapter),
                    messageBlocks.First.Value.GetNewestSequenceToken(cacheDataAdapter));
            }

            // Find block containing sequence number, starting from the newest and working back to oldest
            LinkedListNode<CachedMessageBlock<TCachedMessage>> node = messageBlocks.First;
            while (true)
            {
                TCachedMessage oldestMessageInBlock = node.Value.OldestMessage;
                if (comparer.Compare(oldestMessageInBlock, sequenceToken) <= 0)
                {
                    break;
                }
                node = node.Next;
            }

            // return cursor from start.
            cursor.CurrentBlock = node;
            cursor.Index = node.Value.GetIndexOfFirstMessageLessThanOrEqualTo(sequenceToken, comparer);
            // if cursor has been idle, move to next message after message specified by sequenceToken  
            if(cursor.State == CursorStates.Idle)
            {
                // if there are more messages in this block, move to next message
                if (!cursor.IsNewestInBlock)
                {
                    cursor.Index++;
                }
                // if this is the newest message in this block, move to oldest message in newer block
                else if (node.Previous != null)
                {
                    cursor.CurrentBlock = node.Previous;
                    cursor.Index = cursor.CurrentBlock.Value.OldestMessageIndex;
                }
                else
                {
                    cursor.State = CursorStates.Idle;
                    return;
                }
            }
            cursor.SequenceToken = cursor.CurrentBlock.Value.GetSequenceToken(cursor.Index, cacheDataAdapter);
            cursor.State = CursorStates.Set;
        }

        /// <summary>
        /// Acquires the next message in the cache at the provided cursor
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message)
        {
            message = null;

            if (cursorObj == null)
            {
                throw new ArgumentNullException("cursorObj");
            }

            var cursor = cursorObj as Cursor;
            if (cursor == null)
            {
                throw new ArgumentOutOfRangeException("cursorObj", "Cursor is bad");
            }

            if (cursor.State != CursorStates.Set)
            {
                SetCursor(cursor, cursor.SequenceToken);
                if (cursor.State != CursorStates.Set)
                {
                    return false;
                }
            }

            // has this message been purged
            TCachedMessage oldestMessage = messageBlocks.Last.Value.OldestMessage;
            if (comparer.Compare(oldestMessage, cursor.SequenceToken) > 0)
            {
                throw new QueueCacheMissException(cursor.SequenceToken,
                    messageBlocks.Last.Value.GetOldestSequenceToken(cacheDataAdapter),
                    messageBlocks.First.Value.GetNewestSequenceToken(cacheDataAdapter));
            }

            // Iterate forward (in time) in the cache until we find a message on the stream or run out of cached messages.
            // Note that we get the message from the current cursor location, then move it forward.  This means that if we return true, the cursor
            //   will point to the next message after the one we're returning.
            while (cursor.State == CursorStates.Set)
            {
                TCachedMessage currentMessage = cursor.Message;

                // Have we caught up to the newest event, if so set cursor to idle.
                if (cursor.CurrentBlock == messageBlocks.First && cursor.IsNewestInBlock)
                {
                    cursor.State = CursorStates.Idle;
                    cursor.SequenceToken = messageBlocks.First.Value.GetNewestSequenceToken(cacheDataAdapter);
                }
                else // move to next
                {
                    int index;
                    if (cursor.IsNewestInBlock)
                    {
                        cursor.CurrentBlock = cursor.CurrentBlock.Previous;
                        cursor.CurrentBlock.Value.TryFindFirstMessage(cursor.StreamIdentity, comparer, out index);
                    }
                    else
                    {
                        cursor.CurrentBlock.Value.TryFindNextMessage(cursor.Index + 1, cursor.StreamIdentity, comparer, out index);
                    }
                    cursor.Index = index;
                }

                // check if this message is in the cursor's stream
                if (comparer.Equals(currentMessage, cursor.StreamIdentity))
                {
                    message = cacheDataAdapter.GetBatchContainer(ref currentMessage);
                    cursor.SequenceToken = cursor.CurrentBlock.Value.GetSequenceToken(cursor.Index, cacheDataAdapter);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Add a list of queue message to the cache 
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="dequeueTime"></param>
        /// <returns></returns>
        public List<StreamPosition> Add(List<TQueueMessage> messages, DateTime dequeueTime)
        {
            var streamPosisions = new List<StreamPosition>();
            foreach (var message in messages)
            {
                streamPosisions.Add(this.Add(message, dequeueTime));
            }
            this.cacheMonitor?.TrackMessagesAdded(messages.Count);
            return streamPosisions;
        }

        /// <summary>
        /// Add a queue message to the cache
        /// </summary>
        /// <param name="message"></param>
        /// <param name="dequeueTimeUtc"></param>
        /// <returns></returns>
        public StreamPosition Add(TQueueMessage message, DateTime dequeueTimeUtc)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            StreamPosition streamPosition;
            // allocate message from pool
            CachedMessageBlock<TCachedMessage> block = pool.AllocateMessage(message, dequeueTimeUtc, out streamPosition);

            // If new block, add message block to linked list
            if (block != messageBlocks.FirstOrDefault())
                messageBlocks.AddFirst(block.Node);
            itemCount++;
            return streamPosition;
        }

        /// <summary>
        /// Remove oldest message in the cache, remove oldest block too if the block is empty
        /// </summary>
        public void RemoveOldestMessage()
        {
            this.messageBlocks.Last.Value.Remove();
            this.itemCount--;
            CachedMessageBlock<TCachedMessage> lastCachedMessageBlock = this.messageBlocks.Last.Value;
            // if block is currently empty, but all capacity has been exausted, remove
            if (lastCachedMessageBlock.IsEmpty && !lastCachedMessageBlock.HasCapacity)
            {
                lastCachedMessageBlock.Dispose();
                this.messageBlocks.RemoveLast();
            }
        }

        private enum CursorStates
        {
            Unset, // Not yet set, or points to some data in the future.
            Set, // Points to a message in the cache
            Idle, // Has iterated over all relevant events in the cache and is waiting for more data on the stream.
        }

        private class Cursor
        {
            public readonly IStreamIdentity StreamIdentity;

            public Cursor(IStreamIdentity streamIdentity)
            {
                StreamIdentity = streamIdentity;
                State = CursorStates.Unset;
            }

            public CursorStates State;

            // current sequence token
            public StreamSequenceToken SequenceToken;

            // reference into cache
            public LinkedListNode<CachedMessageBlock<TCachedMessage>> CurrentBlock;
            public int Index;

            // utilities
            public bool IsNewestInBlock => Index == CurrentBlock.Value.NewestMessageIndex;
            public TCachedMessage Message => CurrentBlock.Value[Index];
        }
    }
}
