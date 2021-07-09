using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Cursor into a simple queue cache
    /// </summary>
    public class SimpleQueueCacheCursor : IQueueCacheCursor
    {
        private readonly StreamId streamId;
        private readonly SimpleQueueCache cache;
        private readonly ILogger logger;
        private IBatchContainer current; // this is a pointer to the current element in the cache. It is what will be returned by GetCurrent().

        // This is also a pointer to the current element in the cache. It differs from current, in
        // that current is just the batch, and is null before the first call to MoveNext after
        // construction. (Or after refreshing if we had previously run out of batches). Upon MoveNext
        // being called in that situation, current gets set to the batch included in Element. That is
        // needed to implement the Enumerator pattern properly, since in that pattern MoveNext gets called
        // before the first access of (Get)Current.

        internal LinkedListNode<SimpleQueueCacheItem> Element { get; private set; }
        internal StreamSequenceToken SequenceToken { get; private set; }

        internal bool IsSet => Element != null;

        internal void Set(LinkedListNode<SimpleQueueCacheItem> item)
        {
            if (item == null) throw new NullReferenceException(nameof(item));
            Element = item;
            SequenceToken = item.Value.SequenceToken;
        }

        internal void UnSet(StreamSequenceToken token)
        {
            Element = null;
            SequenceToken = token;
        }

        /// <summary>
        /// Cursor into a simple queue cache
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="streamId"></param>
        /// <param name="logger"></param>
        public SimpleQueueCacheCursor(SimpleQueueCache cache, StreamId streamId, ILogger logger)
        {
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }
            this.cache = cache;
            this.streamId = streamId;
            this.logger = logger;
            current = null;
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("SimpleQueueCacheCursor New Cursor for {StreamId}", streamId);
            }
        }


        /// <summary>
        /// Get the current value.
        /// </summary>
        /// <param name="exception"></param>
        /// <returns>
        /// Returns the current batch container.
        /// If null then the stream has completed or there was a stream error.  
        /// If there was a stream error, an error exception will be provided in the output.
        /// </returns>
        public virtual IBatchContainer GetCurrent(out Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("SimpleQueueCacheCursor.GetCurrent: {Current}", current);
            }

            exception = null;
            return current;
        }

        /// <summary>
        /// Move to next message in the stream.
        /// If it returns false, there are no more messages.  The enumerator is still
        ///  valid however and can be called again when more data has come in on this
        ///  stream.
        /// </summary>
        /// <returns></returns>
        public virtual bool MoveNext()
        {
            if (current == null && IsSet && IsInStream(Element.Value.Batch))
            {
                current = Element.Value.Batch;
                return true;
            }

            IBatchContainer next;
            while (cache.TryGetNextMessage(this, out next))
            {
                if(IsInStream(next))
                    break;
            }
            current = next;
            if (!IsInStream(next))
                return false;

            return true;
        }

        /// <summary>
        /// Refresh that cache cursor. Called when new data is added into a cache.
        /// </summary>
        /// <returns></returns>
        public virtual void Refresh(StreamSequenceToken sequenceToken)
        {
            if (!IsSet)
            {
                cache.RefreshCursor(this, sequenceToken);
            }
        }

        /// <summary>
        /// Record that delivery of the current event has failed
        /// </summary>
        public void RecordDeliveryFailure()
        {
            if (IsSet && current != null)
            {
                Element.Value.DeliveryFailure = true;
            }
        }

        private bool IsInStream(IBatchContainer batchContainer)
        {
            return batchContainer != null &&
                    batchContainer.StreamId.Equals(this.streamId);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Clean up cache data when done
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                cache.UnsetCursor(this, null);
                current = null;
            }
        }

        /// <summary>
        /// Convert object to string
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return $"<SimpleQueueCacheCursor: Element={Element?.Value.Batch.ToString() ?? "null"}, SequenceToken={SequenceToken?.ToString() ?? "null"}>";
        }
    }
}
