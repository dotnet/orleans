/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    internal class CacheBucket
    {
        // For backpressure detection we maintain a histogram of 10 buckets.
        // Every buckets recors how many items are in the cache in that bucket
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

    internal struct SimpleQueueCacheItem
    {
        internal IBatchContainer Batch;
        internal StreamSequenceToken SequenceToken;
        internal CacheBucket CacheBucket;
    }

    public class SimpleQueueCache : IQueueCache
    {
        private readonly LinkedList<SimpleQueueCacheItem> cachedMessages;
        private StreamSequenceToken lastSequenceTokenAddedToCache;
        private readonly int maxCacheSize;
        private readonly Logger logger;
        private readonly List<CacheBucket> cacheCursorHistogram; // for backpressure detection
        private const int NUM_CACHE_HISTOGRAM_BUCKETS = 10;
        private readonly int CACHE_HISTOGRAM_MAX_BUCKET_SIZE;

        public QueueId Id { get; private set; }

        public int Size 
        {
            get { return cachedMessages.Count; }
        }

        public int MaxAddCount
        {
            get { return CACHE_HISTOGRAM_MAX_BUCKET_SIZE; }
        }

        public SimpleQueueCache(QueueId queueId, int cacheSize, Logger logger)
        {
            Id = queueId;
            cachedMessages = new LinkedList<SimpleQueueCacheItem>();
            maxCacheSize = cacheSize;
            
            this.logger = logger;
            cacheCursorHistogram = new List<CacheBucket>();
            CACHE_HISTOGRAM_MAX_BUCKET_SIZE = Math.Max(cacheSize / NUM_CACHE_HISTOGRAM_BUCKETS, 1); // we have 10 buckets
        }

        public bool IsUnderPressure()
        {
            if (cachedMessages.Count == 0) return false; // empty cache
            if (Size < maxCacheSize) return false; // there is still space in cache
            if (cacheCursorHistogram.Count == 0) return false;    // no cursors yet - zero consumers basically yet.
            // cache is full. Check how many cursors we have in the oldest bucket.
            int numCursorsInLastBucket = cacheCursorHistogram[0].NumCurrentCursors;
            return numCursorsInLastBucket > 0;
        }

        public virtual void AddToCache(IList<IBatchContainer> msgs)
        {
            if (msgs == null) throw new ArgumentNullException("msgs");

            Log(logger, "AddToCache: added {0} items to cache.", msgs.Count);
            foreach (var message in msgs)
            {
                Add(message, message.SequenceToken);
                lastSequenceTokenAddedToCache = message.SequenceToken;
            }
        }

        public virtual IQueueCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token)
        {
            if (token != null && !(token is EventSequenceToken))
            {
                // Null token can come from a stream subscriber that is just interested to start consuming from latest (the most recent event added to the cache).
                throw new ArgumentOutOfRangeException("token", "token must be of type EventSequenceToken");
            }

            var cursor = new SimpleQueueCacheCursor(this, streamGuid, streamNamespace, logger);
            InitializeCursor(cursor, token);
            return cursor;
        }

        private void InitializeCursor(SimpleQueueCacheCursor cursor, StreamSequenceToken sequenceToken)
        {
            Log(logger, "InitializeCursor: {0} to sequenceToken {1}", cursor, sequenceToken);
           
            if (cachedMessages.Count == 0) // nothing in cache
            {
                StreamSequenceToken tokenToReset = sequenceToken ?? (lastSequenceTokenAddedToCache != null ? ((EventSequenceToken)lastSequenceTokenAddedToCache).NextSequenceNumber() : null);
                ResetCursor(cursor, tokenToReset);
                return;
            }

            // if offset is not set, iterate from newest (first) message in cache, but not including the irst message itself
            if (sequenceToken == null)
            {
                StreamSequenceToken tokenToReset = lastSequenceTokenAddedToCache != null ? ((EventSequenceToken)lastSequenceTokenAddedToCache).NextSequenceNumber() : null;
                ResetCursor(cursor, tokenToReset);
                return;
            }

            if (sequenceToken.Newer(cachedMessages.First.Value.SequenceToken)) // sequenceId is too new to be in cache
            {
                ResetCursor(cursor, sequenceToken);
                return;
            }

            LinkedListNode<SimpleQueueCacheItem> lastMessage = cachedMessages.Last;
            // Check to see if offset is too old to be in cache
            if (sequenceToken.Older(lastMessage.Value.SequenceToken))
            {
                // throw cache miss exception
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

            if (cursor == null) throw new ArgumentNullException("cursor");
            
            //if not set, try to set and then get next
            if (!cursor.IsSet)
            {
                InitializeCursor(cursor, cursor.SequenceToken);
                return cursor.IsSet && TryGetNextMessage(cursor, out batch);
            }

            // has this message been purged
            if (cursor.SequenceToken.Older(cachedMessages.Last.Value.SequenceToken))
            {
                throw new QueueCacheMissException(cursor.SequenceToken, cachedMessages.Last.Value.SequenceToken, cachedMessages.First.Value.SequenceToken);
            }

            // Cursor now points to a valid message in the cache. Get it!
            // Capture the current element and advance to the next one.
            batch = cursor.Element.Value.Batch;
            
            // Advance to next:
            if (cursor.Element == cachedMessages.First)
            {
                // If we are at the end of the cache unset cursor and move offset one forward
                ResetCursor(cursor, ((EventSequenceToken)cursor.SequenceToken).NextSequenceNumber());
            }
            else // move to next
            {
                UpdateCursor(cursor, cursor.Element.Previous);
            }
            return true;
        }

        private void UpdateCursor(SimpleQueueCacheCursor cursor, LinkedListNode<SimpleQueueCacheItem> item)
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

        internal void ResetCursor(SimpleQueueCacheCursor cursor, StreamSequenceToken token)
        {
            Log(logger, "ResetCursor: {0} to token {1}", cursor, token);

            if (cursor.IsSet)
            {
                cursor.Element.Value.CacheBucket.UpdateNumCursors(-1);
            }
            cursor.Reset(token);
        }

        private void Add(IBatchContainer batch, StreamSequenceToken sequenceToken)
        {
            if (batch == null) throw new ArgumentNullException("batch");

            CacheBucket cacheBucket = null;
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

            cacheBucket.UpdateNumItems(1);
            // Add message to linked list
            var item = new SimpleQueueCacheItem
            {
                Batch = batch,
                SequenceToken = sequenceToken,
                CacheBucket = cacheBucket
            };

            cachedMessages.AddFirst(new LinkedListNode<SimpleQueueCacheItem>(item));

            if (Size > maxCacheSize)
            {
                //var last = cachedMessages.Last;
                cachedMessages.RemoveLast();
                var bucket = cacheCursorHistogram[0]; // same as:  var bucket = last.Value.CacheBucket;
                bucket.UpdateNumItems(-1);
                if (bucket.NumCurrentItems == 0)
                {
                    cacheCursorHistogram.RemoveAt(0);
                }
            }
        }

        internal static void Log(Logger logger, string format, params object[] args)
        {
            if (logger.IsVerbose) logger.Verbose(format, args);
            //if(logger.IsInfo) logger.Info(format, args);
        }
    }
}
