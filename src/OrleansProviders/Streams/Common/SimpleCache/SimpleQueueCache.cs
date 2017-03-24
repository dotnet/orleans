using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    internal class CacheBucket
    {
        // For backpressure detection we maintain a histogram of 10 buckets.
        // Every bucket records how many items are in the cache in that bucket
        // and how many cursors are poinmting to an item in that bucket.
        // We update the NumCurrentItems when we add and remove cache item (potentially opening or removing a bucket)
        // We update NumCurrentCursors every time we move a cursor
        // If the first (most outdated bucket) has at least one cursor pointing to it, we say we are under back pressure (in a full cache).
        internal int NumCurrentItems { get; private set; }
        internal int NumCurrentCursors { get; private set; }

        internal void UpdateNumItems(int val)
        {
            NumCurrentItems = NumCurrentItems + val;
        }
        internal void UpdateNumCursors(int val)
        {
            NumCurrentCursors = NumCurrentCursors + val;
        }
    }

    internal class SimpleQueueCacheItem
    {
        internal IBatchContainer Batch;
        internal bool DeliveryFailure;
        internal StreamSequenceToken SequenceToken;
        internal CacheBucket CacheBucket;
    }

    /// <summary>
    /// A queue cache that keeps items in memory
    /// </summary>
    public class SimpleQueueCache : IQueueCache
    {
        private readonly LinkedList<SimpleQueueCacheItem> cachedMessages;
        private readonly int maxCacheSize;
        private readonly Logger logger;
        private readonly List<CacheBucket> cacheCursorHistogram; // for backpressure detection
        private const int NUM_CACHE_HISTOGRAM_BUCKETS = 10;
        private readonly int CACHE_HISTOGRAM_MAX_BUCKET_SIZE;

        /// <summary>
        /// Number of items in the cache
        /// </summary>
        public int Size => cachedMessages.Count;

        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        public int GetMaxAddCount()
        {
            return CACHE_HISTOGRAM_MAX_BUCKET_SIZE;
        }

        /// <summary>
        /// SimpleQueueCache Constructor
        /// </summary>
        /// <param name="cacheSize"></param>
        /// <param name="logger"></param>
        public SimpleQueueCache(int cacheSize, Logger logger)
        {
            cachedMessages = new LinkedList<SimpleQueueCacheItem>();
            maxCacheSize = cacheSize;
            
            this.logger = logger;
            cacheCursorHistogram = new List<CacheBucket>();
            CACHE_HISTOGRAM_MAX_BUCKET_SIZE = Math.Max(cacheSize / NUM_CACHE_HISTOGRAM_BUCKETS, 1); // we have 10 buckets
        }

        /// <summary>
        /// Returns true if this cache is under pressure.
        /// </summary>
        public virtual bool IsUnderPressure()
        {
            return cacheCursorHistogram.Count >= NUM_CACHE_HISTOGRAM_BUCKETS;
        }


        /// <summary>
        /// Ask the cache if it has items that can be purged from the cache 
        /// (so that they can be subsequently released them the underlying queue).
        /// </summary>
        /// <param name="purgedItems"></param>
        public virtual bool TryPurgeFromCache(out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;
            if (cachedMessages.Count == 0) return false; // empty cache
            if (cacheCursorHistogram.Count == 0) return false;  // no cursors yet - zero consumers basically yet.
            if (cacheCursorHistogram[0].NumCurrentCursors > 0) return false; // consumers are still active in the oldest bucket - fast path

            var allItems = new List<IBatchContainer>();
            while (cacheCursorHistogram.Count > 0 && cacheCursorHistogram[0].NumCurrentCursors == 0)
            {
                List<IBatchContainer> items = DrainBucket(cacheCursorHistogram[0]);
                allItems.AddRange(items);
                cacheCursorHistogram.RemoveAt(0); // remove the last bucket
            }
            purgedItems = allItems;
            Log(logger, "TryPurgeFromCache: purged {0} items from cache.", purgedItems.Count);
            return true;
        }

        private List<IBatchContainer> DrainBucket(CacheBucket bucket)
        {
            var itemsToRelease = new List<IBatchContainer>(bucket.NumCurrentItems);
            // walk all items in the cache starting from last
            // and remove from the cache the oness that reside in the given bucket until we jump to a next bucket
            while (bucket.NumCurrentItems > 0)
            {
                SimpleQueueCacheItem item = cachedMessages.Last.Value;
                if (item.CacheBucket.Equals(bucket))
                {
                    if (!item.DeliveryFailure)
                    {
                        itemsToRelease.Add(item.Batch);
                    }
                    bucket.UpdateNumItems(-1);
                    cachedMessages.RemoveLast();
                }
                else
                {
                    // this item already points into the next bucket, so stop.
                    break;
                }
            }
            return itemsToRelease;
        }

        /// <summary>
        /// Add a list of message to the cache
        /// </summary>
        /// <param name="msgs"></param>
        public virtual void AddToCache(IList<IBatchContainer> msgs)
        {
            if (msgs == null) throw new ArgumentNullException(nameof(msgs));

            Log(logger, "AddToCache: added {0} items to cache.", msgs.Count);
            foreach (var message in msgs)
            {
                Add(message, message.SequenceToken);
            }
        }

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retreave messages from the
        ///   cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamIdentity"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public virtual IQueueCacheCursor GetCacheCursor(IStreamIdentity streamIdentity, StreamSequenceToken token)
        {
            var cursor = new SimpleQueueCacheCursor(this, streamIdentity, logger);
            InitializeCursor(cursor, token);
            return cursor;
        }

        internal void InitializeCursor(SimpleQueueCacheCursor cursor, StreamSequenceToken sequenceToken)
        {
            Log(logger, "InitializeCursor: {0} to sequenceToken {1}", cursor, sequenceToken);

            // Nothing in cache, unset token, and wait for more data.
            if (cachedMessages.Count == 0)
            {
                UnsetCursor(cursor, sequenceToken);
                return;
            }

            // if no token is provided, set cursor to idle at end of cache
            if (sequenceToken == null)
            {
                UnsetCursor(cursor, cachedMessages.First?.Value?.SequenceToken);
                return;
            }

            // If sequenceToken is too new to be in cache, unset token, and wait for more data.
            if (sequenceToken.Newer(cachedMessages.First.Value.SequenceToken))
            {
                UnsetCursor(cursor, sequenceToken);
                return;
            }

            LinkedListNode<SimpleQueueCacheItem> lastMessage = cachedMessages.Last;
            // Check to see if offset is too old to be in cache
            if (sequenceToken.Older(lastMessage.Value.SequenceToken))
            {
                throw new QueueCacheMissException(sequenceToken, cachedMessages.Last.Value.SequenceToken, cachedMessages.First.Value.SequenceToken);
            }

            // Now the requested sequenceToken is set and is also within the limits of the cache.

            // Find first message at or below offset
            // Events are ordered from newest to oldest, so iterate from start of list until we hit a node at a previous offset, or the end.
            LinkedListNode<SimpleQueueCacheItem> node = cachedMessages.First;
            while (node != null && node.Value.SequenceToken.Newer(sequenceToken))
            {
                // did we get to the end?
                if (node.Next == null) // node is the last message
                    break;

                // if sequenceId is between the two, take the higher
                if (node.Next.Value.SequenceToken.Older(sequenceToken))
                    break;

                node = node.Next;
            }

            // return cursor from start.
            SetCursor(cursor, node);
        }

        internal void RefreshCursor(SimpleQueueCacheCursor cursor, StreamSequenceToken sequenceToken)
        {
            Log(logger, "RefreshCursor: {0} to sequenceToken {1}", cursor, sequenceToken);

            // set if unset
            if (!cursor.IsSet)
            {
                InitializeCursor(cursor, cursor.SequenceToken ?? sequenceToken);
            }
        }

        /// <summary>
        /// Aquires the next message in the cache at the provided cursor
        /// </summary>
        /// <param name="cursor"></param>
        /// <param name="batch"></param>
        /// <returns></returns>
        internal bool TryGetNextMessage(SimpleQueueCacheCursor cursor, out IBatchContainer batch)
        {
            Log(logger, "TryGetNextMessage: {0}", cursor);

            batch = null;
            if (!cursor.IsSet) return false;

            // Capture the current element and advance to the next one.
            batch = cursor.Element.Value.Batch;

            // If we are at the end of the cache unset cursor and move offset one forward
            if (cursor.Element == cachedMessages.First)
            {
                UnsetCursor(cursor, null);
            }
            else // Advance to next:
            {
                AdvanceCursor(cursor, cursor.Element.Previous);
            }
            return true;
        }

        private void AdvanceCursor(SimpleQueueCacheCursor cursor, LinkedListNode<SimpleQueueCacheItem> item)
        {
            Log(logger, "UpdateCursor: {0} to item {1}", cursor, item.Value.Batch);

            cursor.Element.Value.CacheBucket.UpdateNumCursors(-1); // remove from prev bucket
            cursor.Set(item);
            cursor.Element.Value.CacheBucket.UpdateNumCursors(1);  // add to next bucket
        }

        internal void SetCursor(SimpleQueueCacheCursor cursor, LinkedListNode<SimpleQueueCacheItem> item)
        {
            Log(logger, "SetCursor: {0} to item {1}", cursor, item.Value.Batch);

            cursor.Set(item);
            cursor.Element.Value.CacheBucket.UpdateNumCursors(1);  // add to next bucket
        }

        internal void UnsetCursor(SimpleQueueCacheCursor cursor, StreamSequenceToken token)
        {
            Log(logger, "UnsetCursor: {0}", cursor);

            if (cursor.IsSet)
            {
                cursor.Element.Value.CacheBucket.UpdateNumCursors(-1);
            }
            cursor.UnSet(token);
        }

        private void Add(IBatchContainer batch, StreamSequenceToken sequenceToken)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            // this should never happen, but just in case
            if (Size >= maxCacheSize) throw new CacheFullException();

            CacheBucket cacheBucket;
            if (cacheCursorHistogram.Count == 0)
            {
                cacheBucket = new CacheBucket();
                cacheCursorHistogram.Add(cacheBucket);
            }
            else
            {
                cacheBucket = cacheCursorHistogram[cacheCursorHistogram.Count - 1]; // last one
            }

            if (cacheBucket.NumCurrentItems == CACHE_HISTOGRAM_MAX_BUCKET_SIZE) // last bucket is full, open a new one
            {
                cacheBucket = new CacheBucket();
                cacheCursorHistogram.Add(cacheBucket);
            }

            // Add message to linked list
            var item = new SimpleQueueCacheItem
            {
                Batch = batch,
                SequenceToken = sequenceToken,
                CacheBucket = cacheBucket
            };

            cachedMessages.AddFirst(new LinkedListNode<SimpleQueueCacheItem>(item));
            cacheBucket.UpdateNumItems(1);
        }

        internal static void Log(Logger logger, string format, params object[] args)
        {
            if (logger.IsVerbose) logger.Verbose(format, args);
            //if(logger.IsInfo) logger.Info(format, args);
        }
    }
}
