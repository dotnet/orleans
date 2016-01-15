using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    public class SimpleQueueCacheCursor : IQueueCacheCursor
    {
        private readonly Guid streamGuid;
        private readonly string streamNamespace;
        private readonly SimpleQueueCache cache;
        private readonly Logger logger;
        private IBatchContainer current; // this is a pointer to the current element in the cache. It is what will be returned by GetCurrent().

        // This is a pointer to the NEXT element in the cache.
        // After the cursor is first created it should be called MoveNext before the call to GetCurrent().
        // After MoveNext returns, the current points to the current element that will be returned by GetCurrent()
        // and Element will point to the next element (since MoveNext actualy advanced it to the next).
        internal LinkedListNode<SimpleQueueCacheItem> Element { get; private set; }
        internal StreamSequenceToken SequenceToken { get; private set; }

        internal bool IsSet
        {
            get { return Element != null; }
        }

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

        public SimpleQueueCacheCursor(SimpleQueueCache cache, Guid streamGuid, string streamNamespace, Logger logger)
        {
            if (cache == null)
            {
                throw new ArgumentNullException("cache");
            }
            this.cache = cache;
            this.streamGuid = streamGuid;
            this.streamNamespace = streamNamespace;
            this.logger = logger;
            current = null;
            SimpleQueueCache.Log(logger, "SimpleQueueCacheCursor New Cursor for {0}, {1}", streamGuid, streamNamespace);
        }

        public virtual IBatchContainer GetCurrent(out Exception exception)
        {
            SimpleQueueCache.Log(logger, "SimpleQueueCacheCursor.GetCurrent: {0}", current);

            exception = null;
            return current;
        }

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

        public virtual void Refresh()
        {
            if (!IsSet)
            {
                cache.InitializeCursor(this, SequenceToken, false);
            }
        }

        private bool IsInStream(IBatchContainer batchContainer)
        {
            return batchContainer != null &&
                    batchContainer.StreamGuid.Equals(streamGuid) &&
                    String.Equals(batchContainer.StreamNamespace, streamNamespace);
        }

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                cache.ResetCursor(this, null);
            }
        }

        #endregion

        public override string ToString()
        {
            return string.Format("<SimpleQueueCacheCursor: Element={0}, SequenceToken={1}>",
                Element != null ? Element.Value.Batch.ToString() : "null", SequenceToken != null ? SequenceToken.ToString() : "null");
        }
    }
}
