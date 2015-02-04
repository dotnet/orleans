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

﻿using System;
using System.Collections.Generic;

using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    public class SimpleQueueAdapterCache : IQueueAdapterCache
    {
        private struct CacheItem
        {
            public IBatchContainer Batch;
            public EventSequenceToken SequenceToken;
        }
        private readonly LinkedList<CacheItem> messages = new LinkedList<CacheItem>();
        private readonly int cacheSize;
        private uint cachedMessageCount;

        private class Cursor
        {
            public bool IsUnset { get; set; }
            public LinkedListNode<CacheItem> Current { get; set; }
            public EventSequenceToken SequenceToken { get; set; }
            public IBatchContainer Message { get { return Current.Value.Batch; } }
        }

        public SimpleQueueAdapterCache(int cacheSize)
        {
            this.cacheSize = cacheSize;
        }

        /// <summary>
        /// Acquires a cursor to enumerate through the _messages starting at the provided token.
        /// </summary>
        /// <param name="sequenceToken"></param>
        /// <returns></returns>
        public object GetCursor(StreamSequenceToken sequenceToken)
        {
            var cursor = new Cursor();
            SetCursor(cursor, (EventSequenceToken)sequenceToken);
            return cursor;
        }

        private void SetCursor(Cursor cursor, EventSequenceToken sequenceToken)
        {
            
            if (messages.Count == 0 || // nothing in cache or
                messages.First.Value.SequenceToken.CompareTo(sequenceToken) < 0) // sequenceId is too new to be in cache
            {
                cursor.IsUnset = true;
                cursor.SequenceToken = sequenceToken;
                return;
            }

            LinkedListNode<CacheItem> lastMessage = messages.Last;

            // if offset of -1, iterate from last message in cache
            if (sequenceToken.IsInvalid())
            {
                cursor.IsUnset = false;
                cursor.Current = lastMessage;
                cursor.SequenceToken = lastMessage.Value.SequenceToken;
                return;
            }

            // Check to see if offset is too old to be in cache
            if (lastMessage.Value.SequenceToken.CompareTo(sequenceToken) > 0)
            {
                // throw cache miss exception
                throw new QueueAdapterCacheMissException(sequenceToken, lastMessage.Value.SequenceToken, messages.First.Value.SequenceToken);
            }

            // Find first message at or below offset
            // Events are ordered from newest to oldest, so iterate from start of list until we hit a node at a previous offset, or the end.
            LinkedListNode<CacheItem> node = messages.First;
            while (node != null && node.Value.SequenceToken.CompareTo(sequenceToken) > 0)
            {
                // did we get to the end?
                if (node == lastMessage)
                    break;
                
                // if sequenceId is between the two, take the higher
                if (node.Next.Value.SequenceToken.CompareTo(sequenceToken) < 0)
                    break;
                
                node = node.Next;
            }

            // return cursor from start.
            cursor.IsUnset = false;
            cursor.Current = node;
            cursor.SequenceToken = node.Value.SequenceToken;
        }

        /// <summary>
        /// Aquires the next message in the cache at the provided cursor
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="batch"></param>
        /// <param name="backPressure">Indicates how much backpressure this cursor should exert (0-100)</param>
        /// <returns></returns>
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer batch, out double backPressure)
        {
            batch = null;
            backPressure = 0;

            if (cursorObj == null) throw new ArgumentNullException("cursorObj");
            
            var cursor = cursorObj as Cursor;
            if (cursor == null)
                throw new ArgumentOutOfRangeException("cursorObj", "Cursor is bad");
            
            //if unset, try to set and then get next
            if (cursor.IsUnset)
            {
                SetCursor(cursor, cursor.SequenceToken);
                return !cursor.IsUnset && TryGetNextMessage(cursor, out batch, out backPressure);
            }

            // has this message been purged
            if (cursor.SequenceToken.CompareTo(messages.Last.Value.SequenceToken) < 0)
            {
                throw new QueueAdapterCacheMissException(cursor.SequenceToken, messages.Last.Value.SequenceToken, messages.First.Value.SequenceToken);
            }

            // get message
            batch = cursor.Message;
            backPressure = Math.Min(1.0, Math.Max(((double)EventSequenceToken.Distance(messages.First.Value.SequenceToken, cursor.SequenceToken) / cachedMessageCount), 0.0));

            // are we up to date? if so unset cursor, and move offset one forward
            if (cursor.Current == messages.First)
            {
                cursor.IsUnset = true;
                cursor.SequenceToken = cursor.SequenceToken.NextSequenceNumber();
            }
            else // move to next
            {
                cursor.Current = cursor.Current.Previous;
                cursor.SequenceToken = cursor.Current.Value.SequenceToken;
            }

            return true;
        }

        public void Add(IBatchContainer batch, StreamSequenceToken sequenceToken)
        {
            if (batch == null) throw new ArgumentNullException("batch");
            
            // Add message to linked list
            var item = new CacheItem
            {
                Batch = batch,
                SequenceToken = (EventSequenceToken)sequenceToken
            };
            messages.AddFirst(new LinkedListNode<CacheItem>(item));

            if (cachedMessageCount < cacheSize)
                cachedMessageCount++;
            else
                messages.RemoveLast();
        }
    }
}