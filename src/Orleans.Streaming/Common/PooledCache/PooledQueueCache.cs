
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
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
    public class PooledQueueCache : IPurgeObservable
    {
        // linked list of message bocks.  First is newest.
        private readonly LinkedList<CachedMessageBlock> messageBlocks;
        private readonly CachedMessagePool pool;
        private readonly ICacheDataAdapter cacheDataAdapter;
        private readonly ILogger logger;
        private readonly ICacheMonitor? cacheMonitor;
        private readonly TimeSpan purgeMetadataInterval;
        private readonly TimeSpan? monitorWriteInterval;
        private DateTime nextMonitorWriteTime;
        private readonly PeriodicAction? periodicMetadaPurging;

        private readonly Dictionary<StreamId, (DateTime TimeStamp, StreamSequenceToken Token)> lastPurgedToken = new Dictionary<StreamId, (DateTime TimeStamp, StreamSequenceToken Token)>();

        /// <summary>
        /// Gets the cached message most recently added.
        /// </summary>
        public CachedMessage? Newest
        {
            get
            {
                if (IsEmpty)
                    return null;
                return messageBlocks.First!.Value.NewestMessage; // messageBlocks.Count != 0 here (checked above via IsEmpty).
            }
        }

        /// <summary>
        /// Gets the oldest message in cache.
        /// </summary>
        public CachedMessage? Oldest
        {
            get
            {
                if (IsEmpty)
                    return null;
                return messageBlocks.Last!.Value.OldestMessage; // messageBlocks.Count != 0 here (checked above via IsEmpty).
            }
        }

        /// <summary>
        /// Gets the cached message count.
        /// </summary>
        public int ItemCount { get; private set; }

        /// <summary>
        /// Pooled queue cache is a cache of message that obtains resource from a pool
        /// </summary>
        /// <param name="cacheDataAdapter">The cache data adapter.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="cacheMonitor">The cache monitor.</param>
        /// <param name="cacheMonitorWriteInterval">The cache monitor write interval. Only triggered for active caches.</param>
        /// <param name="purgeMetadataInterval">The interval after which to purge cache metadata.</param>
        public PooledQueueCache(
            ICacheDataAdapter cacheDataAdapter,
            ILogger logger,
            ICacheMonitor? cacheMonitor,
            TimeSpan? cacheMonitorWriteInterval,
            TimeSpan? purgeMetadataInterval = null)
        {
            this.cacheDataAdapter = cacheDataAdapter ?? throw new ArgumentNullException(nameof(cacheDataAdapter));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.ItemCount = 0;
            pool = new CachedMessagePool(cacheDataAdapter);
            messageBlocks = new LinkedList<CachedMessageBlock>();
            this.cacheMonitor = cacheMonitor;
            if (this.cacheMonitor != null && cacheMonitorWriteInterval.HasValue)
            {
                this.monitorWriteInterval = cacheMonitorWriteInterval;
                this.nextMonitorWriteTime = DateTime.UtcNow + cacheMonitorWriteInterval.Value;
            }

            if (purgeMetadataInterval.HasValue)
            {
                this.purgeMetadataInterval = purgeMetadataInterval.Value;
                this.periodicMetadaPurging = new PeriodicAction(purgeMetadataInterval.Value.Divide(5), this.PurgeMetadata);
            }
        }

        /// <summary>
        /// Indicates whether the cache is empty
        /// </summary>
        public bool IsEmpty => messageBlocks.Count == 0 || (messageBlocks.Count == 1 && messageBlocks.First!.Value.IsEmpty); // messageBlocks.Count == 1 implies First is non-null.

        /// <summary>
        /// Acquires a cursor to enumerate through the messages in the cache at the provided sequenceToken, 
        ///   filtered on the specified stream.
        /// </summary>
        /// <param name="streamId">stream identity</param>
        /// <param name="sequenceToken"></param>
        /// <returns>The acquired cache cursor.</returns>
        /// <exception cref="QueueCacheMissException">
        /// The requested token is older than the messages retained by the cache.
        /// </exception>
        [Obsolete("Use TryGetCursor instead.")]
        public object GetCursor(StreamId streamId, StreamSequenceToken? sequenceToken)
        {
            var result = TryGetCursor(streamId, sequenceToken);
            if (result.CacheMiss is { } cacheMiss)
            {
                throw cacheMiss.ToException();
            }

            return result.Cursor!;
        }

        /// <summary>
        /// Attempts to acquire a cursor at the provided sequence token.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="sequenceToken">The sequence token.</param>
        /// <returns>
        /// A successful result containing the acquired cursor, or a cache-miss result containing the
        /// unavailable position and current cache bounds.
        /// </returns>
        public QueueCacheCursorResult<object> TryGetCursor(StreamId streamId, StreamSequenceToken? sequenceToken)
        {
            if (TryGetCacheMiss(streamId, sequenceToken, out var cacheMiss))
            {
                return QueueCacheCursorResult<object>.FromCacheMiss(cacheMiss);
            }

            var cursor = new Cursor(this, streamId)
            {
                InclusiveStartToken = sequenceToken,
            };
            if (SetCursor(cursor, sequenceToken) is { } racedCacheMiss)
            {
                return QueueCacheCursorResult<object>.FromCacheMiss(racedCacheMiss);
            }

            return QueueCacheCursorResult<object>.FromCursor(cursor);
        }

        /// <summary>
        /// Attempts to acquire a cursor at the specified subscription start position.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="startPosition">The initial subscription position.</param>
        /// <returns>A successful result containing the acquired cursor.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPosition"/> is not defined.</exception>
        public QueueCacheCursorResult<object> TryGetCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            if (startPosition == StreamSubscriptionStartPosition.Latest)
            {
                return TryGetCursor(streamId, null);
            }

            if (startPosition != StreamSubscriptionStartPosition.EarliestAvailable)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition), startPosition, "The subscription start position is not defined.");
            }

            var result = new Cursor(this, streamId);
            SetCursorAtEarliestAvailable(result);
            return QueueCacheCursorResult<object>.FromCursor(result);
        }

        /// <summary>
        /// Repositions an idle or unset cursor at the provided sequence token.
        /// </summary>
        /// <param name="cursorObj">The cursor to refresh.</param>
        /// <param name="sequenceToken">The sequence token to position the cursor at.</param>
        public void Refresh(object cursorObj, StreamSequenceToken? sequenceToken)
        {
            ArgumentNullException.ThrowIfNull(cursorObj);

            if (cursorObj is not Cursor cursor)
            {
                throw new ArgumentOutOfRangeException(nameof(cursorObj), "Cursor is bad");
            }

            if (cursor.CacheMiss is { } observedMiss)
            {
                throw observedMiss.ToException();
            }

            if (cursor.IsEarliestAvailable || cursor.RetryPendingDelivery)
            {
                return;
            }

            // Only reposition idle or unset cursors. An active (Set) cursor is mid-enumeration and must not be moved.
            // A null token has no reposition target, so leave the cursor untouched and let the normal idle wake-up
            // path handle it, rather than repositioning to the newest event (which would skip data).
            if (sequenceToken is null || cursor.State == CursorStates.Set)
            {
                return;
            }

            // Refresh only ever moves a cursor forward. If the cursor is already positioned at or ahead of the
            // requested token (for example an unset cursor waiting on a future token), leave it in place so we do
            // not rewind it and re-deliver events the consumer has already requested to start after.
            if (cursor.SequenceToken is not null && EventSequenceTokenCompatibility.Compare(cursor.SequenceToken, sequenceToken) >= 0)
            {
                return;
            }

            cursor.State = CursorStates.Unset;
            cursor.InclusiveStartToken = sequenceToken;
            if (SetCursor(cursor, sequenceToken) is { } cacheMiss)
            {
                cursor.CacheMiss = cacheMiss;
                throw cacheMiss.ToException();
            }
        }

        private void ReportCacheMessageStatistics(List<CachedMessage> messages, int projectedItemCount)
        {
            var oldestMessage = Oldest;
            var newestMessage = Newest;
            if (messages.Count > 0)
            {
                oldestMessage ??= messages[0];
                newestMessage = messages[^1];
            }

            // Preserve post-admission metric values without exposing a committed prefix to
            // a potentially throwing observer. This method is only called with a monitor.
            cacheMonitor!.ReportMessageStatistics(
                oldestMessage?.EnqueueTimeUtc,
                oldestMessage?.DequeueTimeUtc,
                newestMessage?.EnqueueTimeUtc,
                projectedItemCount);
        }

        private void PurgeMetadata()
        {
            var now = DateTime.UtcNow;

            // Get all keys older than this.purgeMetadataInterval
            foreach (var kvp in this.lastPurgedToken)
            {
                if (kvp.Value.TimeStamp + this.purgeMetadataInterval < now)
                {
                    lastPurgedToken.Remove(kvp.Key);
                }
            }
        }

        private void TrackAndPurgeMetadata(CachedMessage messageToRemove)
        {
            // If tracking of evicted message metadata is disabled, do nothing
            if (this.periodicMetadaPurging == null)
                return;

            var now = DateTime.UtcNow;
            var streamId = messageToRemove.StreamId;
            var token = this.cacheDataAdapter.GetSequenceToken(ref messageToRemove);
            this.lastPurgedToken[streamId] = (now, token);

            this.periodicMetadaPurging.TryAction(now);
        }

        private QueueCacheMissInfo? SetCursor(Cursor cursor, StreamSequenceToken? sequenceToken)
        {
            // If nothing in cache, unset token and wait for more data.
            if (IsEmpty)
            {
                cursor.State = sequenceToken is null ? CursorStates.Idle : CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return null;
            }

            LinkedListNode<CachedMessageBlock> newestBlock = messageBlocks.First!; // messageBlocks.Count != 0 (checked above).

            // A cursor which waited on an empty cache includes the first admitted message.
            // A new Latest cursor excludes the entire existing partition prefix.
            if (sequenceToken == null)
            {
                if (cursor.State == CursorStates.Idle)
                {
                    var waitingOldestBlock = messageBlocks.Last!;
                    cursor.State = CursorStates.Set;
                    cursor.CurrentBlock = waitingOldestBlock;
                    cursor.Index = waitingOldestBlock.Value.OldestMessageIndex;
                    cursor.SequenceToken = waitingOldestBlock.Value.GetOldestSequenceToken(cacheDataAdapter);
                }
                else
                {
                    cursor.State = CursorStates.Idle;
                    cursor.CurrentBlock = newestBlock;
                    cursor.Index = newestBlock.Value.NewestMessageIndex;
                    cursor.SequenceToken = newestBlock.Value.GetNewestSequenceToken(cacheDataAdapter);
                    cursor.RecordScanned(cursor.SequenceToken);
                }

                return null;
            }

            // The retained partition prefix precedes the requested start, so it is safe to scan
            // while keeping the inclusive target pending for a later read.
            CachedMessage newestMessage = newestBlock.Value.NewestMessage;
            if (cacheDataAdapter.Compare(ref newestMessage, sequenceToken) < 0)
            {
                cursor.RecordScanned(cacheDataAdapter.GetSequenceToken(ref newestMessage));
                cursor.State = CursorStates.Unset;
                cursor.SequenceToken = sequenceToken;
                return null;
            }

            // Check to see if sequenceToken is too old to be in cache
            var oldestBlock = messageBlocks.Last!; // messageBlocks.Count != 0 (checked above).
            var oldestMessage = oldestBlock.Value.OldestMessage;
            if (cacheDataAdapter.Compare(ref oldestMessage, sequenceToken) > 0)
            {
                // Check if we missed an event since we last purged the cache
                if (this.lastPurgedToken.TryGetValue(cursor.StreamId, out var entry) && EventSequenceTokenCompatibility.Compare(sequenceToken, entry.Token) >= 0)
                {
                    // If the token is more recent than the last purged token, then we didn't lose anything. Start from the oldest message in cache
                    cursor.State = CursorStates.Set;
                    cursor.CurrentBlock = oldestBlock;
                    cursor.Index = oldestBlock.Value.OldestMessageIndex;
                    cursor.SequenceToken = oldestBlock.Value.GetOldestSequenceToken(cacheDataAdapter);
                    return null;
                }
                else
                {
                    return CreateCacheMissInfo(sequenceToken);
                }
            }

            // Find block containing sequence number, starting from the newest and working back to oldest
            LinkedListNode<CachedMessageBlock>? node = messageBlocks.First;
            while (true)
            {
                CachedMessage oldestMessageInBlock = node!.Value.OldestMessage; // Loop invariant: node is non-null while the search has not exhausted the cache (guaranteed by the bounds checks above).
                if (cacheDataAdapter.Compare(ref oldestMessageInBlock, sequenceToken) <= 0)
                {
                    break;
                }
                node = node.Next;
            }

            // return cursor from start.
            cursor.CurrentBlock = node;
            cursor.Index = node!.Value.GetIndexOfFirstMessageLessThanOrEqualTo(sequenceToken, cacheDataAdapter); // See loop invariant above.
            // if cursor has been idle, move to next message after message specified by sequenceToken  
            if (cursor.State == CursorStates.Idle)
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
                    cursor.Index = cursor.CurrentBlock!.Value.OldestMessageIndex; // Just assigned to node.Previous, which is non-null here.
                }
                else
                {
                    cursor.State = CursorStates.Idle;
                    return null;
                }
            }
            cursor.SequenceToken = cursor.CurrentBlock!.Value.GetSequenceToken(cursor.Index, cacheDataAdapter); // CurrentBlock was set to a valid block above in every path that reaches this point.
            cursor.State = CursorStates.Set;
            return null;
        }

        private bool TryGetCacheMiss(
            StreamId streamId,
            StreamSequenceToken? sequenceToken,
            out QueueCacheMissInfo cacheMiss)
        {
            if (sequenceToken is null || IsEmpty)
            {
                cacheMiss = default;
                return false;
            }

            var newestBlock = messageBlocks.First!;
            var newestMessage = newestBlock.Value.NewestMessage;
            if (cacheDataAdapter.Compare(ref newestMessage, sequenceToken) < 0)
            {
                cacheMiss = default;
                return false;
            }

            var oldestMessage = messageBlocks.Last!.Value.OldestMessage;
            if (cacheDataAdapter.Compare(ref oldestMessage, sequenceToken) <= 0
                || lastPurgedToken.TryGetValue(streamId, out var entry)
                    && EventSequenceTokenCompatibility.Compare(sequenceToken, entry.Token) >= 0)
            {
                cacheMiss = default;
                return false;
            }

            cacheMiss = CreateCacheMissInfo(sequenceToken);
            return true;
        }

        private void SetCursorAtEarliestAvailable(Cursor cursor)
        {
            if (IsEmpty)
            {
                SetWaitingAtCurrentEnd(cursor);
                return;
            }

            var oldestBlock = messageBlocks.Last!;
            cursor.State = CursorStates.EarliestAvailableSet;
            cursor.CurrentBlock = oldestBlock;
            cursor.Index = oldestBlock.Value.OldestMessageIndex;
            cursor.SequenceToken = oldestBlock.Value.GetOldestSequenceToken(cacheDataAdapter);
            cursor.BlockGeneration = oldestBlock.Value.Generation;
        }

        private void SetWaitingAtCurrentEnd(Cursor cursor)
        {
            cursor.State = CursorStates.EarliestAvailableWaiting;
            cursor.SequenceToken = null;
            cursor.CurrentBlock = messageBlocks.First;
            cursor.BlockGeneration = cursor.CurrentBlock?.Value.Generation ?? 0;
            cursor.Index = cursor.CurrentBlock?.Value.WriteIndex ?? 0;
        }

        private static void SetWaitingAfter(Cursor cursor, LinkedListNode<CachedMessageBlock> block, int index)
        {
            cursor.State = CursorStates.EarliestAvailableWaiting;
            cursor.SequenceToken = null;
            cursor.CurrentBlock = block;
            cursor.BlockGeneration = block.Value.Generation;
            cursor.Index = index + 1;
        }

        private bool TrySetCursorAfterAnchor(Cursor cursor)
        {
            LinkedListNode<CachedMessageBlock>? node;
            int startIndex;
            if (cursor.CurrentBlock is { } anchor
                && anchor.List == messageBlocks
                && anchor.Value.Generation == cursor.BlockGeneration)
            {
                node = anchor;
                startIndex = Math.Max(cursor.Index, anchor.Value.OldestMessageIndex);
            }
            else
            {
                node = messageBlocks.Last;
                startIndex = node?.Value.OldestMessageIndex ?? 0;
            }

            while (node is not null)
            {
                if (!node.Value.IsEmpty
                    && startIndex < node.Value.WriteIndex)
                {
                    cursor.State = CursorStates.EarliestAvailableSet;
                    cursor.CurrentBlock = node;
                    cursor.Index = startIndex;
                    cursor.SequenceToken = node.Value.GetSequenceToken(startIndex, cacheDataAdapter);
                    cursor.BlockGeneration = node.Value.Generation;
                    return true;
                }

                node = node.Previous;
                startIndex = node?.Value.OldestMessageIndex ?? 0;
            }

            SetWaitingAtCurrentEnd(cursor);
            return false;
        }

        /// <summary>
        /// Acquires the next message in the cache at the provided cursor
        /// </summary>
        /// <param name="cursorObj"></param>
        /// <param name="message"></param>
        /// <returns><see langword="true"/> when a message was returned; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="cursorObj"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursorObj"/> is not a cache cursor.</exception>
        /// <exception cref="QueueCacheMissException">
        /// The cursor position is older than the messages retained by the cache.
        /// </exception>
        [Obsolete("Use TryGetNextMessageWithResult instead.")]
        public bool TryGetNextMessage(object cursorObj, [NotNullWhen(true)] out IBatchContainer? message)
        {
            var result = TryGetNextMessageWithResult(cursorObj, out message);
            if (result.CacheMiss is { } cacheMiss)
            {
                throw cacheMiss.ToException();
            }

            return result.Kind == QueueCacheCursorMoveResultKind.Success;
        }

        /// <summary>
        /// Attempts to acquire the next message in the cache at the provided cursor.
        /// </summary>
        /// <param name="cursorObj">The cache cursor.</param>
        /// <param name="message">The next message when one is available.</param>
        /// <returns>
        /// A successful result with a non-null <paramref name="message"/>, <see cref="QueueCacheCursorMoveResultKind.NoData"/>
        /// with a null message, or a cache-miss result with a null message.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="cursorObj"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursorObj"/> is not a cache cursor.</exception>
        public QueueCacheCursorMoveResult TryGetNextMessageWithResult(object cursorObj, out IBatchContainer? message)
        {
            message = null;

            if (cursorObj == null)
            {
                throw new ArgumentNullException(nameof(cursorObj));
            }

            var cursor = cursorObj as Cursor;
            if (cursor == null)
            {
                throw new ArgumentOutOfRangeException(nameof(cursorObj), "Cursor is bad");
            }

            if (cursor.CacheMiss is { } observedMiss)
            {
                return QueueCacheCursorMoveResult.FromCacheMiss(observedMiss);
            }

            if (cursor.RetryPendingDelivery)
            {
                if (RewindPendingDelivery(cursor) is { } retryCacheMiss)
                {
                    return RecordCacheMiss(cursor, retryCacheMiss);
                }
            }

            if (IsEmpty)
            {
                if (cursor.State is CursorStates.Set or CursorStates.EarliestAvailableSet)
                {
                    // A previously positioned unread record was purged. Empty bounds are
                    // unknown, not evidence that the cursor caught up without loss.
                    return RecordCacheMiss(cursor, CreateCacheMissInfo(cursor.SequenceToken!));
                }

                return QueueCacheCursorMoveResult.NoData;
            }

            if (cursor.State == CursorStates.EarliestAvailableSet
                && (cursor.CurrentBlock?.List != messageBlocks
                    || !cursor.CurrentBlock.Value.Contains(cursor.Index, cursor.BlockGeneration)))
            {
                if (cursor.HasScanned)
                {
                    // EarliestAvailable only tolerates eviction before its initial scan.
                    // Once scanning begins, losing the unread position is an explicit gap.
                    return RecordCacheMiss(cursor, CreateCacheMissInfo(cursor.SequenceToken!));
                }

                if (cursor.CurrentBlock is { } currentBlock
                    && currentBlock.List == messageBlocks
                    && currentBlock.Value.Generation == cursor.BlockGeneration)
                {
                    SetWaitingAfter(cursor, currentBlock, cursor.Index);
                }
                else
                {
                    cursor.CurrentBlock = null;
                    cursor.BlockGeneration = 0;
                    cursor.Index = 0;
                    cursor.State = CursorStates.EarliestAvailableWaiting;
                }
            }

            if (cursor.State == CursorStates.EarliestAvailableWaiting
                && !TrySetCursorAfterAnchor(cursor))
            {
                return QueueCacheCursorMoveResult.NoData;
            }

            if (cursor.State is not CursorStates.Set and not CursorStates.EarliestAvailableSet)
            {
                if (SetCursor(cursor, cursor.SequenceToken) is { } cacheMiss)
                {
                    return RecordCacheMiss(cursor, cacheMiss);
                }

                if (cursor.State != CursorStates.Set)
                {
                    return QueueCacheCursorMoveResult.NoData;
                }
            }

            // has this message been purged
            CachedMessage oldestMessage = messageBlocks.Last!.Value.OldestMessage; // Cursor is Set, so the cache is non-empty.
            if (cursor.State == CursorStates.Set
                && cacheDataAdapter.Compare(ref oldestMessage, cursor.SequenceToken!) > 0) // Cursor is Set, so SequenceToken is guaranteed non-null.
            {
                return RecordCacheMiss(cursor, CreateCacheMissInfo(cursor.SequenceToken!));
            }

            // Iterate forward in partition order. Records for other streams are safe as soon as they
            // are scanned. A matching record and everything after it remain pending until its delivery
            // is confirmed by the owner.
            while (cursor.State is CursorStates.Set or CursorStates.EarliestAvailableSet)
            {
                cursor.HasScanned = true;
                CachedMessage currentMessage = cursor.Message;
                var currentToken = cacheDataAdapter.GetSequenceToken(ref currentMessage);

                // check if this message is in the cursor's stream
                if (currentMessage.CompareStreamId(cursor.StreamId))
                {
                    if (cursor.DeliveredThroughToken is { } deliveredThrough
                        && cacheDataAdapter.Compare(ref currentMessage, deliveredThrough) < 0)
                    {
                        MoveCursorForward(cursor, currentToken);
                        if (cursor.TrackDeliveryProgress) cursor.RecordScanned(currentToken);
                        continue;
                    }

                    try
                    {
                        message = cacheDataAdapter.GetBatchContainer(ref currentMessage);
                        if (cursor.DeliveredThroughToken is { } exclusiveStartToken
                            && cacheDataAdapter.Compare(ref currentMessage, exclusiveStartToken) == 0)
                        {
                            if (message is not IQueueCacheBatchContainerFilter exclusiveFilter
                                || exclusiveFilter.FilterAfter(exclusiveStartToken) is not { } filtered)
                            {
                                MoveCursorForward(cursor, currentToken);
                                if (cursor.TrackDeliveryProgress) cursor.RecordScanned(exclusiveStartToken);
                                message = null;
                                continue;
                            }

                            message = filtered;
                        }

                        if (cursor.InclusiveStartToken is { } inclusiveStartToken)
                        {
                            if (message is IQueueCacheBatchContainerFilter filter)
                            {
                                message = filter.FilterFrom(inclusiveStartToken);
                                if (message is null)
                                {
                                    MoveCursorForward(cursor, currentToken);
                                    if (cursor.TrackDeliveryProgress) cursor.RecordScanned(currentToken);
                                    continue;
                                }
                            }
                        }

                        if (cursor.TrackDeliveryProgress) cursor.RecordPending(message.SequenceToken, currentToken);
                    }
                    catch
                    {
                        // Materialization and provider slicing must leave the first selected
                        // record recoverable, even before a batch is returned to the owner.
                        if (cursor.TrackDeliveryProgress) cursor.RecordPending(currentToken);
                        throw;
                    }

                    MoveCursorForward(cursor, currentToken);
                    return QueueCacheCursorMoveResult.Success;
                }

                MoveCursorForward(cursor, currentToken);
                if (cursor.TrackDeliveryProgress) cursor.RecordScanned(currentToken);
            }

            return QueueCacheCursorMoveResult.NoData;
        }

        private static QueueCacheCursorMoveResult RecordCacheMiss(Cursor cursor, QueueCacheMissInfo cacheMiss)
        {
            cursor.CacheMiss ??= cacheMiss;
            return QueueCacheCursorMoveResult.FromCacheMiss(cursor.CacheMiss.Value);
        }

        private QueueCacheMissInfo CreateCacheMissInfo(StreamSequenceToken requestedToken)
            => IsEmpty
                ? new(requestedToken.ToString(), low: null, high: null)
                : new(
                    requestedToken,
                    messageBlocks.Last!.Value.GetOldestSequenceToken(cacheDataAdapter),
                    messageBlocks.First!.Value.GetNewestSequenceToken(cacheDataAdapter));

        internal StreamSequenceToken? GetSafeSequenceToken(object cursorObj)
            => GetCursor(cursorObj).SafeSequenceToken;

        internal void EnableDeliveryProgress(object cursorObj)
            => GetCursor(cursorObj).TrackDeliveryProgress = true;

        internal void SetCursorDeliveredThrough(object cursorObj, StreamSequenceToken token)
        {
            var cursor = GetCursor(cursorObj);
            cursor.DeliveredThroughToken = token;
            cursor.InclusiveStartToken = null;
        }

        internal void RecordDeliveryCompletion(object cursorObj)
            => GetCursor(cursorObj).RecordDeliveryCompletion();

        internal void AbandonPendingDelivery(object cursorObj)
        {
            var cursor = GetCursor(cursorObj);
            cursor.TakePendingStartToken();
            cursor.InclusiveStartToken = null;
            cursor.DeliveredThroughToken = null;
        }

        internal void RecordDeliveryFailure(object cursorObj)
        {
            var cursor = GetCursor(cursorObj);
            if (cursor.CacheMiss is { } observedMiss)
            {
                throw observedMiss.ToException();
            }

            if (cursor.PendingStartToken is null)
            {
                return;
            }

            cursor.RetryPendingDelivery = true;
            if (RewindPendingDelivery(cursor) is { } cacheMiss)
            {
                cursor.CacheMiss = cacheMiss;
                throw cacheMiss.ToException();
            }
        }

        private QueueCacheMissInfo? RewindPendingDelivery(Cursor cursor)
        {
            var retryToken = cursor.PendingStartToken!;
            if (IsEmpty)
            {
                return CreateCacheMissInfo(retryToken);
            }

            // A retry must not skip its purged matching record, even if it equals
            // this stream's last-purged token. That is an unresolved gap, not progress.
            var oldestMessage = messageBlocks.Last!.Value.OldestMessage;
            if (cacheDataAdapter.Compare(ref oldestMessage, retryToken) > 0)
            {
                return CreateCacheMissInfo(retryToken);
            }

            cursor.State = CursorStates.Unset;
            cursor.CurrentBlock = null;
            cursor.SequenceToken = retryToken;
            if (SetCursor(cursor, retryToken) is { } cacheMiss)
            {
                return cacheMiss;
            }

            if (cursor.State != CursorStates.Set)
            {
                return CreateCacheMissInfo(retryToken);
            }

            // Preserve the original inclusive/exclusive slicing boundary across retries.
            cursor.TakePendingStartToken();
            cursor.RetryPendingDelivery = false;
            return null;
        }

        private Cursor GetCursor(object cursorObj)
            => cursorObj as Cursor
                ?? throw new ArgumentOutOfRangeException(nameof(cursorObj), "Cursor is bad");

        private void MoveCursorForward(Cursor cursor, StreamSequenceToken currentToken)
        {
            if (cursor.CurrentBlock == messageBlocks.First && cursor.IsNewestInBlock)
            {
                if (cursor.State == CursorStates.EarliestAvailableSet)
                {
                    SetWaitingAfter(cursor, cursor.CurrentBlock!, cursor.Index);
                }
                else
                {
                    cursor.State = CursorStates.Idle;
                    cursor.SequenceToken = currentToken;
                }

                return;
            }

            // Resolve the provider token before mutating the position: an adapter failure
            // must not leave the cursor past an unread record with a stale token.
            var nextBlock = cursor.IsNewestInBlock ? cursor.CurrentBlock!.Previous! : cursor.CurrentBlock!;
            var nextIndex = cursor.IsNewestInBlock ? nextBlock.Value.OldestMessageIndex : cursor.Index + 1;
            var nextToken = nextBlock.Value.GetSequenceToken(nextIndex, cacheDataAdapter);
            cursor.CurrentBlock = nextBlock;
            cursor.Index = nextIndex;

            if (cursor.State == CursorStates.EarliestAvailableSet)
            {
                cursor.BlockGeneration = cursor.CurrentBlock!.Value.Generation;
            }

            cursor.SequenceToken = nextToken;
        }

        /// <summary>
        /// Add a list of queue message to the cache 
        /// </summary>
        /// <param name="messages"></param>
        /// <param name="dequeueTime"></param>
        /// <returns></returns>
        public void Add(List<CachedMessage> messages, DateTime dequeueTime)
        {
            ArgumentNullException.ThrowIfNull(messages);
            var projectedItemCount = checked(ItemCount + messages.Count);
            // Validate deadline arithmetic before any observer or metadata mutation.
            var nextReportTime = monitorWriteInterval is { } interval && dequeueTime >= nextMonitorWriteTime
                ? dequeueTime + interval
                : (DateTime?)null;

            // Once insertion starts, only the internal metadata pool is involved: no
            // provider/adapter or observer callbacks may throw after a partial commit.
            // Allocation failures are outside this recoverable admission contract.
            cacheMonitor?.TrackMessagesAdded(messages.Count);
            if (nextReportTime.HasValue)
            {
                ReportCacheMessageStatistics(messages, projectedItemCount);
            }

            foreach (var message in messages)
            {
                this.Add(message);
            }

            if (nextReportTime is { } next)
            {
                // A failed observer must not suppress reporting when admission is retried
                // at the same timestamp. Advance the schedule only after the commit.
                nextMonitorWriteTime = next;
            }
        }

        private void Add(CachedMessage message)
        {
            // allocate message from pool
            CachedMessageBlock block = pool.AllocateMessage(message);

            // If new block, add message block to linked list
            if (block != messageBlocks.FirstOrDefault())
                messageBlocks.AddFirst(block.Node);
            ItemCount++;
        }

        /// <summary>
        /// Remove oldest message in the cache, remove oldest block too if the block is empty
        /// </summary>
        public void RemoveOldestMessage()
        {
            TrackAndPurgeMetadata(this.messageBlocks.Last!.Value.OldestMessage); // Only called when the cache is non-empty.

            this.messageBlocks.Last!.Value.Remove(); // Only called when the cache is non-empty.
            this.ItemCount--;
            CachedMessageBlock lastCachedMessageBlock = this.messageBlocks.Last!.Value; // Only called when the cache is non-empty.
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
            EarliestAvailableSet, // Points to a message selected by insertion order for an earliest-available subscription.
            EarliestAvailableWaiting, // Waits after a physical cache position for the next matching stream message.
        }

        private class Cursor : IQueueCacheCursorProgress
        {
            private readonly PooledQueueCache owner;
            public readonly StreamId StreamId;

            public Cursor(PooledQueueCache owner, StreamId streamId)
            {
                this.owner = owner;
                StreamId = streamId;
                State = CursorStates.Unset;
            }

            public CursorStates State;
            public bool TrackDeliveryProgress;
            public bool HasScanned;

            void IQueueCacheCursorProgress.EnableDeliveryProgress() => TrackDeliveryProgress = true;

            StreamSequenceToken? IQueueCacheCursorProgress.SafeSequenceToken => SafeSequenceToken;

            void IQueueCacheCursorProgress.SetDeliveredThrough(StreamSequenceToken token)
                => owner.SetCursorDeliveredThrough(this, token);

            void IQueueCacheCursorProgress.RecordDeliveryFailure()
                => owner.RecordDeliveryFailure(this);

            // current sequence token; null while waiting for the first message to arrive
            public StreamSequenceToken? SequenceToken;
            public StreamSequenceToken? InclusiveStartToken;
            public long BlockGeneration;
            public StreamSequenceToken? SafeSequenceToken;
            public StreamSequenceToken? DeliveredThroughToken;
            private StreamSequenceToken? pendingSequenceToken;
            private StreamSequenceToken? pendingStartToken;
            private bool hasPendingDelivery;
            public bool RetryPendingDelivery;
            // Observed loss is sticky until the owner deliberately acquires a new cursor.
            public QueueCacheMissInfo? CacheMiss;
            public StreamSequenceToken? PendingStartToken => pendingStartToken;

            // reference into cache; non-null while State is Set
            public LinkedListNode<CachedMessageBlock>? CurrentBlock;
            public int Index;

            // utilities
            public bool IsEarliestAvailable => State is CursorStates.EarliestAvailableSet or CursorStates.EarliestAvailableWaiting;
            public bool IsNewestInBlock => Index == CurrentBlock!.Value.NewestMessageIndex; // Only accessed while State is Set, at which point CurrentBlock is non-null.
            public CachedMessage Message => CurrentBlock!.Value[Index]; // Only accessed while State is Set, at which point CurrentBlock is non-null.

            public void RecordScanned(StreamSequenceToken token)
            {
                if (hasPendingDelivery)
                {
                    pendingSequenceToken = token;
                }
                else
                {
                    SafeSequenceToken = token;
                }
            }

            public void RecordPending(StreamSequenceToken token, StreamSequenceToken? retryToken = null)
            {
                if (!hasPendingDelivery)
                {
                    pendingStartToken = retryToken ?? token;
                }

                hasPendingDelivery = true;
                pendingSequenceToken = token;
            }

            public void RecordDeliveryCompletion()
            {
                if (!hasPendingDelivery || RetryPendingDelivery || CacheMiss.HasValue)
                {
                    return;
                }

                SafeSequenceToken = pendingSequenceToken;
                pendingSequenceToken = null;
                pendingStartToken = null;
                hasPendingDelivery = false;
                InclusiveStartToken = null;
                DeliveredThroughToken = null;
            }

            public StreamSequenceToken? TakePendingStartToken()
            {
                if (!hasPendingDelivery)
                {
                    return null;
                }

                var result = pendingStartToken;
                pendingSequenceToken = null;
                pendingStartToken = null;
                hasPendingDelivery = false;
                return result;
            }

        }
    }
}
