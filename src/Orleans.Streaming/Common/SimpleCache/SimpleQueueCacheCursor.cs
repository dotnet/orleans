using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Cursor into a simple queue cache.
    /// </summary>
    public partial class SimpleQueueCacheCursor : IQueueCacheCursor, IQueueCacheCursorBatchDelivery
    {
        private readonly StreamId streamId;
        private readonly SimpleQueueCache cache;
        private readonly ILogger logger;
        private IBatchContainer? current; // this is a pointer to the current element in the cache. It is what will be returned by GetCurrent().
        private DeliveryBatch? deliveryBatch;

        // This is also a pointer to the current element in the cache. It differs from current, in
        // that current is just the batch, and is null before the first call to MoveNext after
        // construction. (Or after refreshing if we had previously run out of batches). Upon MoveNext
        // being called in that situation, current gets set to the batch included in Element. That is
        // needed to implement the Enumerator pattern properly, since in that pattern MoveNext gets called
        // before the first access of (Get)Current.

        internal LinkedListNode<SimpleQueueCacheItem>? Element { get; private set; }
        internal StreamSequenceToken? SequenceToken { get; private set; }
        internal bool WaitingForEarliestAvailable { get; set; }

        internal bool IsSet => Element != null;

        internal void Set(LinkedListNode<SimpleQueueCacheItem> item)
        {
            if (item == null) throw new NullReferenceException(nameof(item));
            Element = item;
            SequenceToken = item.Value.SequenceToken;
        }

        internal void UnSet(StreamSequenceToken? token)
        {
            Element = null;
            SequenceToken = token;
        }

        /// <summary>
        /// Cursor into a simple queue cache
        /// </summary>
        /// <param name="cache">The cache instance.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="logger">The logger.</param>
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
            LogDebugNewCursor(streamId);
        }

        /// <inheritdoc />
        public virtual IBatchContainer? GetCurrent(out Exception? exception)
        {
            LogDebugGetCurrent(current);

            exception = null;
            return current;
        }

        /// <inheritdoc />
        public virtual bool MoveNext()
        {
            if (current == null && IsSet && IsInStream(Element!.Value.Batch)) // IsSet is true, so Element is non-null.
            {
                current = Element!.Value.Batch;
                deliveryBatch?.Track(Element);
                return true;
            }

            IBatchContainer? next;
            while (cache.TryGetNextMessage(this, out next))
            {
                if (IsInStream(next))
                    break;
            }
            current = next;
            if (!IsInStream(next))
                return false;

            deliveryBatch?.Track(Element!);
            return true;
        }

        /// <inheritdoc />
        public virtual void Refresh(StreamSequenceToken sequenceToken)
        {
            if (!IsSet)
            {
                cache.RefreshCursor(this, sequenceToken);
            }
        }

        /// <inheritdoc />
        public void RecordDeliveryFailure()
        {
            if (IsSet && current != null)
            {
                Element!.Value.DeliveryFailure = true; // IsSet is true, so Element is non-null.
            }
        }

        IDisposable IQueueCacheCursorBatchDelivery.ProtectDeliveryBatch()
        {
            if (deliveryBatch is not null)
            {
                throw new InvalidOperationException("A delivery batch is already active for this cursor.");
            }

            return deliveryBatch = new DeliveryBatch(this);
        }

        void IQueueCacheCursorBatchDelivery.RecordDeliveryFailure(IBatchContainer batch)
        {
            if (deliveryBatch is null)
            {
                throw new InvalidOperationException("No delivery batch is active for this cursor.");
            }

            deliveryBatch.RecordDeliveryFailure(batch);
        }

        internal bool IsInStream(IBatchContainer? batchContainer)
        {
            return batchContainer != null &&
                    batchContainer.StreamId.Equals(this.streamId);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Clean up cache data when done
        /// </summary>
        /// <param name="disposing"><see langword="true"/> if the instance is being disposed; <see langword="false"/> if it is being called from a finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                deliveryBatch?.Dispose();
                cache.UnsetCursor(this, null);
                current = null;
            }
        }

        private sealed class DeliveryBatch : IDisposable
        {
            private readonly SimpleQueueCacheCursor owner;
            private readonly CacheBucket? pinnedBucket;
            private readonly LinkedListNode<SimpleQueueCacheItem>? firstElement;
            private LinkedListNode<SimpleQueueCacheItem>? lastElement;
            private bool disposed;

            public DeliveryBatch(SimpleQueueCacheCursor owner)
            {
                this.owner = owner;
                firstElement = owner.Element;
                pinnedBucket = firstElement?.Value.CacheBucket;
                pinnedBucket?.UpdateNumCursors(1);
            }

            public void Track(LinkedListNode<SimpleQueueCacheItem> item)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                lastElement = item;
            }

            public void RecordDeliveryFailure(IBatchContainer batch)
            {
                ObjectDisposedException.ThrowIf(disposed, this);

                if (batch is IBatchContainerBatch batchGroup)
                {
                    foreach (var item in batchGroup.BatchContainers)
                    {
                        RecordDeliveryFailure(item);
                    }
                }
                else
                {
                    for (var item = firstElement; item is not null; item = item.Previous)
                    {
                        if (ReferenceEquals(item.Value.Batch, batch))
                        {
                            item.Value.DeliveryFailure = true;
                            return;
                        }

                        if (ReferenceEquals(item, lastElement))
                        {
                            break;
                        }
                    }

                    throw new InvalidOperationException("The failed delivery was not read by this cursor.");
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                owner.deliveryBatch = null;
                pinnedBucket?.UpdateNumCursors(-1);
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"<SimpleQueueCacheCursor: Element={Element?.Value.Batch.ToString() ?? "null"}, SequenceToken={SequenceToken?.ToString() ?? "null"}>";
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "SimpleQueueCacheCursor New Cursor for {StreamId}"
        )]
        private partial void LogDebugNewCursor(StreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "SimpleQueueCacheCursor.GetCurrent: {Current}"
        )]
        private partial void LogDebugGetCurrent(IBatchContainer? current);
    }
}
