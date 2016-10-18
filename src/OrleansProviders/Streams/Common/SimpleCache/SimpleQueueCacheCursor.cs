using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Cursor into a simple queue cache
    /// </summary>
    public class SimpleQueueCacheCursor : IQueueCacheCursor
    {
        private readonly IStreamIdentity streamIdentity;
        private readonly SimpleQueueCache cache;
        private readonly Logger logger;
        private IBatchContainer current; // this is a pointer to the current element in the cache. It is what will be returned by GetCurrent().

        // This is a pointer to the NEXT element in the cache.
        // After the cursor is first created it should be called MoveNext before the call to GetCurrent().
        // After MoveNext returns, the current points to the current element that will be returned by GetCurrent()
        // and Element will point to the next element (since MoveNext actualy advanced it to the next).
        internal LinkedListNode<SimpleQueueCacheItem> Element { get; private set; }
        internal StreamSequenceToken SequenceToken { get; private set; }

        internal bool IsSet => Element != null;

        internal void Reset(StreamSequenceToken token)
        {
            Element = null;
            SequenceToken = token;
        }

        internal void Set(LinkedListNode<SimpleQueueCacheItem> item)
        {
            Element = item;
            SequenceToken = item.Value.SequenceToken;
        }

        /// <summary>
        /// Cursor into a simple queue cache
        /// </summary>
        /// <param name="cache"></param>
        /// <param name="streamIdentity"></param>
        /// <param name="logger"></param>
        public SimpleQueueCacheCursor(SimpleQueueCache cache, IStreamIdentity streamIdentity, Logger logger)
        {
            if (cache == null)
            {
                throw new ArgumentNullException("cache");
            }
            this.cache = cache;
            this.streamIdentity = streamIdentity;
            this.logger = logger;
            current = null;
            SimpleQueueCache.Log(logger, "SimpleQueueCacheCursor New Cursor for {0}, {1}", streamIdentity.Guid, streamIdentity.Namespace);
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
            SimpleQueueCache.Log(logger, "SimpleQueueCacheCursor.GetCurrent: {0}", current);

            exception = null;
            return current;
        }

        /// <summary>
        /// Move to next message in the stream.
        /// If it returns false, there are no more messages.  The enumerator is still
        ///  valid howerver and can be called again when more data has come in on this
        ///  stream.
        /// </summary>
        /// <returns></returns>
        public virtual bool MoveNext()
        {
            IBatchContainer next;
            while (cache.TryGetNextMessage(this, out next))
            {
                if(IsInStream(next))
                    break;
            }
            if (!IsInStream(next))
                return false;

            current = next;
            return true;
        }

        /// <summary>
        /// Refresh that cache cursor. Called when new data is added into a cache.
        /// </summary>
        /// <returns></returns>
        public virtual void Refresh()
        {
            if (!IsSet)
            {
                cache.InitializeCursor(this, SequenceToken, false);
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
                    batchContainer.StreamGuid.Equals(streamIdentity.Guid) &&
                    string.Equals(batchContainer.StreamNamespace, streamIdentity.Namespace);
        }

        #region IDisposable Members

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
                cache.ResetCursor(this, null);
            }
        }

        #endregion

        /// <summary>
        /// Convert object to string
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return
                $"<SimpleQueueCacheCursor: Element={Element?.Value.Batch.ToString() ?? "null"}, SequenceToken={SequenceToken?.ToString() ?? "null"}>";
        }
    }
}
