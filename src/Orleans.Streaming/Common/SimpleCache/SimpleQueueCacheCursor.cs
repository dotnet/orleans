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
    public partial class SimpleQueueCacheCursor : IQueueCacheCursor, IQueueCacheCursorBatchDelivery, IQueueCacheCursorProgress
    {
        private readonly StreamId streamId;
        private readonly SimpleQueueCache cache;
        private readonly ILogger logger;
        private IBatchContainer? current; // this is a pointer to the current element in the cache. It is what will be returned by GetCurrent().
        private DeliveryBatch? deliveryBatch;
        private StreamSequenceToken? safeSequenceToken;
        private StreamSequenceToken? pendingSequenceToken;
        private StreamSequenceToken? deliveredThroughToken;
        // Pin independently of a delivery scope: read failures can outlive that scope.
        private LinkedListNode<SimpleQueueCacheItem>? firstPendingElement;
        private LinkedListNode<SimpleQueueCacheItem>? lastPendingElement;

        internal StreamSequenceToken? InclusiveStartToken { get; set; }
        internal LinkedListNode<SimpleQueueCacheItem>? WaitingAfter { get; set; }
        internal QueueCacheMissInfo? CacheMiss { get; set; }

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
        [Obsolete("Use MoveNextWithResult instead.")]
        public virtual bool MoveNext()
        {
            if (CacheMiss is { } observedMiss)
            {
                throw observedMiss.ToException();
            }

            if (current is not null)
            {
                current = null;
                cache.TryGetNextMessage(this, out _);
            }

            if (!IsSet)
            {
                cache.RefreshCursor(this, null);
            }

            while (Element is { } item)
            {
                var batch = item.Value.Batch;
                var token = item.Value.SequenceToken;
                if (IsInStream(batch))
                {
                    try
                    {
                        if (deliveredThroughToken is { } deliveredThrough)
                        {
                            var comparison = EventSequenceTokenCompatibility.Compare(token, deliveredThrough);
                            if (batch is IQueueCacheBatchContainerFilter exclusiveFilter)
                            {
                                if (exclusiveFilter.FilterAfter(deliveredThrough) is not { } filtered)
                                {
                                    RecordScanned(token);
                                    cache.TryGetNextMessage(this, out _);
                                    continue;
                                }

                                batch = filtered;
                            }
                            else if (comparison <= 0)
                            {
                                RecordScanned(token);
                                cache.TryGetNextMessage(this, out _);
                                continue;
                            }
                        }

                        if (InclusiveStartToken is { } inclusiveStartToken
                            && batch is IQueueCacheBatchContainerFilter filter)
                        {
                            if (filter.FilterFrom(inclusiveStartToken) is not { } filtered)
                            {
                                RecordScanned(token);
                                cache.TryGetNextMessage(this, out _);
                                continue;
                            }
                            batch = filtered;
                        }

                        RecordPending(item, batch.SequenceToken);
                        deliveryBatch?.Track(item, batch);
                        current = batch;
                        return true;
                    }
                    catch
                    {
                        // Filtering can throw before GetCurrent. Retain the original receipt
                        // and position so the same selection can be attempted again.
                        RecordPending(item, token);
                        throw;
                    }
                }

                RecordScanned(token);
                cache.TryGetNextMessage(this, out _);
            }

            return false;
        }

        /// <inheritdoc />
        public virtual QueueCacheCursorMoveResult MoveNextWithResult()
        {
            if (CacheMiss is { } observedMiss)
            {
                return QueueCacheCursorMoveResult.FromCacheMiss(observedMiss);
            }

            try
            {
#pragma warning disable CS0618 // Preserve virtual dispatch for derived cursors which override the legacy method.
                return MoveNext() ? QueueCacheCursorMoveResult.Success : QueueCacheCursorMoveResult.NoData;
#pragma warning restore CS0618
            }
            catch (QueueCacheMissException exception)
            {
                // Native cache misses already retain provider tokens. Do not replace the
                // first typed result with a lossy string round-trip through the exception.
                // Legacy overrides without native miss state still use the adapter below.
                return QueueCacheCursorMoveResult.FromCacheMiss(
                    CacheMiss ?? new QueueCacheMissInfo(exception.Requested, exception.Low, exception.High));
            }
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
            MarkPendingDeliveryFailure();
            ClearPendingDelivery();
        }

        void IQueueCacheCursorProgress.RecordDeliveryFailure()
        {
            MarkPendingDeliveryFailure();
            RewindPendingDelivery();
        }

        private void MarkPendingDeliveryFailure()
        {
            if (CacheMiss is { } observedMiss)
            {
                throw observedMiss.ToException();
            }

            for (var item = firstPendingElement; item is not null; item = item.Previous)
            {
                if (IsInStream(item.Value.Batch))
                {
                    item.Value.DeliveryFailure = true;
                }

                if (item == lastPendingElement)
                {
                    break;
                }
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
            ClearPendingDelivery();
        }

        StreamSequenceToken? IQueueCacheCursorProgress.SafeSequenceToken => safeSequenceToken;

        void IQueueCacheCursorProgress.SetDeliveredThrough(StreamSequenceToken token)
        {
            deliveredThroughToken = token;
            InclusiveStartToken = null;
        }

        void IQueueCacheCursorProgress.RecordDeliverySuccess()
        {
            if (firstPendingElement is null || CacheMiss.HasValue)
            {
                return;
            }

            safeSequenceToken = pendingSequenceToken;
            ClearPendingDelivery();
            InclusiveStartToken = null;
            deliveredThroughToken = null;
        }

        internal void RecordScanned(StreamSequenceToken token)
        {
            if (firstPendingElement is not null)
            {
                pendingSequenceToken = token;
            }
            else
            {
                safeSequenceToken = token;
            }
        }

        internal void StartAfter(LinkedListNode<SimpleQueueCacheItem>? tail)
        {
            WaitingAfter = tail;
            WaitingForEarliestAvailable = true;
            InclusiveStartToken = null;
            current = null;
            if (tail is not null)
            {
                RecordScanned(tail.Value.SequenceToken);
            }
        }

        private void RecordPending(LinkedListNode<SimpleQueueCacheItem> item, StreamSequenceToken token)
        {
            if (firstPendingElement is null)
            {
                firstPendingElement = item;
                item.Value.CacheBucket.UpdateNumCursors(1);
            }

            lastPendingElement = item;
            pendingSequenceToken = token;
        }

        private void RewindPendingDelivery()
        {
            if (firstPendingElement is not { } first)
            {
                return;
            }

            cache.UnsetCursor(this, null);
            cache.SetCursor(this, first);
            // Transfer protection to the rewound cursor before releasing the pending pin.
            ClearPendingDelivery();
            WaitingAfter = null;
            WaitingForEarliestAvailable = false;
            current = null;
        }

        private void ClearPendingDelivery()
        {
            firstPendingElement?.Value.CacheBucket.UpdateNumCursors(-1);
            firstPendingElement = null;
            lastPendingElement = null;
            pendingSequenceToken = null;
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
                ClearPendingDelivery();
                cache.UnsetCursor(this, null);
                WaitingAfter = null;
                current = null;
            }
        }

        private sealed class DeliveryBatch : IDisposable
        {
            private readonly SimpleQueueCacheCursor owner;
            private CacheBucket? pinnedBucket;
            private readonly List<(LinkedListNode<SimpleQueueCacheItem> Item, IBatchContainer Batch)> batches = [];
            private bool disposed;

            public DeliveryBatch(SimpleQueueCacheCursor owner)
            {
                this.owner = owner;
                pinnedBucket = owner.Element?.Value.CacheBucket;
                pinnedBucket?.UpdateNumCursors(1);
            }

            public void Track(LinkedListNode<SimpleQueueCacheItem> item, IBatchContainer batch)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (pinnedBucket is null)
                {
                    pinnedBucket = item.Value.CacheBucket;
                    pinnedBucket.UpdateNumCursors(1);
                }

                batches.Add((item, batch));
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
                    foreach (var (item, selectedBatch) in batches)
                    {
                        if (ReferenceEquals(selectedBatch, batch) || ReferenceEquals(item.Value.Batch, batch))
                        {
                            item.Value.DeliveryFailure = true;
                            return;
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
