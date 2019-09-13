
using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Abstractions;
using Orleans.Streams;

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
    public class PooledQueueCache: IFiFoEvictableCache<CachedMessage>
    {
        private readonly CachedMessagePool pool;
        private readonly ICacheMonitor cacheMonitor;
        private readonly PeriodicAction periodicMonitoring;
        private CachedMessageBlock First; // newest data is first
        private CachedMessageBlock Last; // oldest data is last

        /// <summary>
        /// Exposed for testing
        /// </summary>
        public int ItemCount { get; private set; }

        /// <summary>
        /// Cached message most recently added
        /// </summary>
        public CachedMessage? Newest => (this.First == null || this.First.IsEmpty)
            ? null
            : (CachedMessage?)this.First.NewestMessage;

        /// <summary>
        /// Oldest message in cache
        /// </summary>
        public CachedMessage? Oldest => (this.First == null || this.First.IsEmpty)
            ? null
            : (CachedMessage?)this.Last.OldestMessage;

        /// <summary>
        /// Pooled queue cache is a cache of message that obtains resource from a pool
        /// </summary>
        /// <param name="cacheMonitor"></param>
        /// <param name="cacheMonitorWriteInterval">cache monitor write interval.  Only triggered for active caches.</param>
        public PooledQueueCache(ICacheMonitor cacheMonitor, TimeSpan? cacheMonitorWriteInterval)
        {
            this.pool = new CachedMessagePool();
            this.cacheMonitor = cacheMonitor;

            if (this.cacheMonitor != null && cacheMonitorWriteInterval.HasValue)
            {
                this.periodicMonitoring = new PeriodicAction(cacheMonitorWriteInterval.Value, this.ReportCacheMessageStatistics);
            }
        }

        /// <summary>
        /// Acquires a cursor to enumerate through the messages in the cache at the provided sequenceToken, 
        ///   filtered on the specified stream.
        /// </summary>
        /// <param name="streamIdentity">stream identity</param>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(byte[] streamIdentity, byte[] sequenceToken)
        {
            var cursor = new Cursor(streamIdentity);
            this.SetCursor(cursor, sequenceToken);
            return cursor;
        }

        private void ReportCacheMessageStatistics()
        {
            if (this.First == null || this.First.IsEmpty)
            {
                this.cacheMonitor.ReportMessageStatistics(null, null, null, this.ItemCount);
            }
            else
            {
                var newestMessage = this.Newest.Value;
                var oldestMessage = this.Oldest.Value;
                var newestMessageEnqueueTime = newestMessage.EnqueueTimeUtc;
                var oldestMessageEnqueueTime = oldestMessage.EnqueueTimeUtc;
                var oldestMessageDequeueTime = oldestMessage.DequeueTimeUtc;
                this.cacheMonitor.ReportMessageStatistics(oldestMessageEnqueueTime, oldestMessageDequeueTime, newestMessageEnqueueTime, this.ItemCount);
            }
        }

        private void SetCursor(Cursor cursor, byte[] sequenceToken)
        {
            // If nothing in cache, unset token, and wait for more data.
            if (this.First == null || this.First.IsEmpty)
            {
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            CachedMessageBlock newestBlock = this.First;

            // if sequenceToken is null, iterate from newest message in cache
            if (sequenceToken == null)
            {
                cursor.State = CursorStates.Idle;
                cursor.CurrentBlock = newestBlock;
                cursor.Index = newestBlock.NewestMessageIndex;
                cursor.SequenceToken = newestBlock.GetNewestSequenceToken().ToArray();
                return;
            }

            // If sequenceToken is too new to be in cache, unset token, and wait for more data.
            CachedMessage newestMessage = newestBlock.NewestMessage;
            if (newestMessage.Compare(sequenceToken) < 0) 
            {
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            // Check to see if sequenceToken is too old to be in cache
            CachedMessage oldestMessage = this.Last.OldestMessage;
            if (oldestMessage.Compare(sequenceToken) > 0)
            {
                // throw cache miss exception
                throw new QueueCacheMissException(sequenceToken,
                    this.Last.GetOldestSequenceToken(),
                    this.First.GetNewestSequenceToken());
            }

            // Find block containing sequence number, starting from the newest and working back to oldest
            CachedMessageBlock node = this.First;
            while (true)
            {
                CachedMessage oldestMessageInBlock = node.OldestMessage;
                if (oldestMessageInBlock.Compare(sequenceToken) <= 0)
                {
                    break;
                }
                node = node.Next;
            }

            // return cursor from start.
            cursor.CurrentBlock = node;
            cursor.Index = node.GetIndexOfFirstMessageLessThanOrEqualTo(sequenceToken);
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
                    cursor.Index = cursor.CurrentBlock.OldestMessageIndex;
                }
                else
                {
                    cursor.State = CursorStates.Idle;
                    return;
                }
            }
            cursor.SequenceToken = cursor.CurrentBlock.GetSequenceToken(cursor.Index).ToArray();
            cursor.State = CursorStates.Set;
        }

        /// <summary>
        /// Acquires the next message in the cache at the provided cursor
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out CachedMessage message)
        {
            message = default;

            if (cursorObj == null)
            {
                throw new ArgumentNullException("cursorObj");
            }

            if (!(cursorObj is Cursor cursor))
            {
                throw new ArgumentOutOfRangeException("cursorObj", "Cursor is bad");
            }

            if (cursor.State != CursorStates.Set)
            {
                this.SetCursor(cursor, cursor.SequenceToken);
                if (cursor.State != CursorStates.Set)
                {
                    return false;
                }
            }

            // has this message been purged
            CachedMessage oldestMessage = this.Last.OldestMessage;
            if (oldestMessage.Compare(cursor.SequenceToken) > 0)
            {
                throw new QueueCacheMissException(cursor.SequenceToken,
                    this.Last.GetOldestSequenceToken(),
                    this.First.GetNewestSequenceToken());
            }

            // Iterate forward (in time) in the cache until we find a message on the stream or run out of cached messages.
            // Note that we get the message from the current cursor location, then move it forward.  This means that if we return true, the cursor
            //   will point to the next message after the one we're returning.
            while (cursor.State == CursorStates.Set)
            {
                CachedMessage currentMessage = cursor.Message;

                // Have we caught up to the newest event, if so set cursor to idle.
                if (cursor.CurrentBlock == this.First && cursor.IsNewestInBlock)
                {
                    cursor.State = CursorStates.Idle;
                    cursor.SequenceToken = this.First.GetNewestSequenceToken().ToArray();
                }
                else // move to next
                {
                    int index;
                    if (cursor.IsNewestInBlock)
                    {
                        cursor.CurrentBlock = cursor.CurrentBlock.Previous;
                        cursor.CurrentBlock.TryFindFirstMessage(cursor.StreamIdentity, out index);
                    }
                    else
                    {
                        cursor.CurrentBlock.TryFindNextMessage(cursor.Index + 1, cursor.StreamIdentity, out index);
                    }
                    cursor.Index = index;
                }

                // check if this message is in the cursor's stream
                if (currentMessage.CompareStreamId(cursor.StreamIdentity))
                {
                    message = currentMessage;
                    cursor.SequenceToken = cursor.CurrentBlock.GetSequenceToken(cursor.Index).ToArray();
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
        public void Add(List<CachedMessage> messages, in DateTime dequeueTime)
        {
            foreach (var message in messages)
            {
                this.Add(message);
            }
            this.cacheMonitor?.TrackMessagesAdded(messages.Count);
            this.periodicMonitoring?.TryAction(dequeueTime);
        }

        private void Add(in CachedMessage message)
        {
            // allocate message from pool
            CachedMessageBlock block = pool.AllocateMessage(message);

            // If new block, add message block to linked list
            if (block != this.First)
            {
                AddFirst(block);
            }
            this.ItemCount++;
        }

        private void AddFirst(CachedMessageBlock block)
        {
            // This is first block, link as first and last
            if(this.First == null)
            {
                this.First = block;
                this.Last = block;
            } else
            {
                block.Next = this.First;
                this.First.Previous = block;
                this.First = block;
            }
        }

        /// <summary>
        /// Remove oldest message in the cache, remove oldest block too if the block is empty
        /// </summary>
        public void RemoveOldestMessage()
        {
            this.Last.Remove();
            this.ItemCount--;
            CachedMessageBlock lastCachedMessageBlock = this.Last;
            // if block is currently empty, and all capacity has been exausted, remove
            if (lastCachedMessageBlock.IsEmpty && !lastCachedMessageBlock.HasCapacity)
            {
                lastCachedMessageBlock.Dispose();
                this.UnlinkLast();
            }
        }

        private void UnlinkLast()
        {
            if (this.Last == null) return;
            if (this.Last == this.First)
            {
                this.Last = this.First = null;
                return;
            }
            this.Last = this.Last.Previous;
            this.Last.Next = null;
        }

        private enum CursorStates
        {
            Unset, // Not yet set, or points to some data in the future.
            Set, // Points to a message in the cache
            Idle, // Has iterated over all relevant events in the cache and is waiting for more data on the stream.
        }

        private class Cursor
        {
            public readonly byte[] StreamIdentity;

            public Cursor(byte[] streamIdentity)
            {
                StreamIdentity = streamIdentity;
                State = CursorStates.Unset;
            }

            public CursorStates State;

            // current sequence token
            public byte[] SequenceToken;

            // reference into cache
            public CachedMessageBlock CurrentBlock;
            public int Index;

            // utilities
            public bool IsNewestInBlock => this.Index == this.CurrentBlock.NewestMessageIndex;
            public ref CachedMessage Message => ref this.CurrentBlock[Index];
        }
    }
}
