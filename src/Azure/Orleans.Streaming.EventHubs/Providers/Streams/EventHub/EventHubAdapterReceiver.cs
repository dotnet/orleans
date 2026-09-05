using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streaming.EventHubs.Testing;
using Azure.Messaging.EventHubs;
using Orleans.Statistics;

namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Event Hub Partition settings
    /// </summary>
    public class EventHubPartitionSettings
    {
        /// <summary>
        /// Eventhub settings
        /// </summary>
        public EventHubOptions Hub { get; set; } = null!;

        /// <summary>
        /// Gets or sets the options which control receiving events from the partition.
        /// </summary>
        public EventHubReceiverOptions ReceiverOptions { get; set; } = null!;

        /// <summary>
        /// Partition name
        /// </summary>
        public string Partition { get; set; } = null!;
    }

    internal partial class EventHubAdapterReceiver : IQueueAdapterReceiver, IQueueCache
    {
        public const int MaxMessagesPerRead = 1000;
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);

        private readonly EventHubPartitionSettings settings;
        private readonly Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, IEventHubQueueCache> cacheFactory;
        private readonly Func<string, CancellationToken, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly IQueueAdapterReceiverMonitor monitor;
        private readonly LoadSheddingOptions loadSheddingOptions;
        private readonly IEnvironmentStatisticsProvider environmentStatisticsProvider;
        private readonly object cacheLock = new();
        private IEventHubQueueCache? cache;

        private IEventHubReceiver? receiver;

        private readonly Func<EventHubPartitionSettings, string, ILogger, IEventHubReceiver> eventHubReceiverFactory;

        private IStreamQueueCheckpointer<string>? checkpointer;
        private AggregatedQueueFlowController flowController = new(MaxMessagesPerRead);
        private bool receiverUsesCheckpoint;
        private IEventHubQueueCache? recoveryCache;
        private Dictionary<Cursor, RecoveredCursorProgress>? recoveredCursorProgress;
        private readonly HashSet<Cursor> cursors = new(ReferenceEqualityComparer.Instance);
        private HashSet<Cursor>? recoveryPendingCursors;

        // Receiver life cycle
        private int receiverState = ReceiverShutdown;

        private const int ReceiverShutdown = 0;
        private const int ReceiverRunning = 1;

        public int GetMaxAddCount()
        {
            lock (this.cacheLock)
            {
                return this.flowController.GetMaxAddCount();
            }
        }

        public EventHubAdapterReceiver(EventHubPartitionSettings settings,
            Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, IEventHubQueueCache> cacheFactory,
            Func<string, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory,
            ILoggerFactory loggerFactory,
            IQueueAdapterReceiverMonitor monitor,
            LoadSheddingOptions loadSheddingOptions,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            Func<EventHubPartitionSettings, string, ILogger, IEventHubReceiver>? eventHubReceiverFactory = null)
            : this(
                settings,
                cacheFactory,
                (partition, _) => checkpointerFactory(partition),
                loggerFactory,
                monitor,
                loadSheddingOptions,
                environmentStatisticsProvider,
                eventHubReceiverFactory)
        {
        }

        public EventHubAdapterReceiver(EventHubPartitionSettings settings,
            Func<string, IStreamQueueCheckpointer<string>, ILoggerFactory, IEventHubQueueCache> cacheFactory,
            Func<string, CancellationToken, Task<IStreamQueueCheckpointer<string>>> checkpointerFactory,
            ILoggerFactory loggerFactory,
            IQueueAdapterReceiverMonitor monitor,
            LoadSheddingOptions loadSheddingOptions,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            Func<EventHubPartitionSettings, string, ILogger, IEventHubReceiver>? eventHubReceiverFactory = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.cacheFactory = cacheFactory ?? throw new ArgumentNullException(nameof(cacheFactory));
            this.checkpointerFactory = checkpointerFactory ?? throw new ArgumentNullException(nameof(checkpointerFactory));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.logger = this.loggerFactory.CreateLogger<EventHubAdapterReceiver>();
            this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            this.loadSheddingOptions = loadSheddingOptions ?? throw new ArgumentNullException(nameof(loadSheddingOptions));
            this.environmentStatisticsProvider = environmentStatisticsProvider;
            this.eventHubReceiverFactory = eventHubReceiverFactory == null ? EventHubAdapterReceiver.CreateReceiver : eventHubReceiverFactory;
        }

        public async Task Initialize(TimeSpan timeout)
        {
            LogInfoInitializingEventHubPartition(this.settings.Hub.EventHubName, this.settings.Partition);

            // if receiver was already running, do nothing
            if (ReceiverRunning == Interlocked.Exchange(ref this.receiverState, ReceiverRunning))
            {
                return;
            }

            using var cancellation = new CancellationTokenSource(timeout);
            await Initialize(cancellation.Token);
        }

        /// <summary>
        /// Initialization of EventHub receiver is performed at adapter receiver initialization, but if it fails,
        ///  it will be retried when messages are requested
        /// </summary>
        /// <returns></returns>
        private async Task Initialize(CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var checkpointer = await this.checkpointerFactory(
                    this.settings.Partition,
                    cancellationToken);
                string offset = await checkpointer.Load(cancellationToken);
                var receiverUsesCheckpoint = checkpointer.CheckpointExists;
                if (!receiverUsesCheckpoint)
                {
                    offset = EventHubConstants.StartOfStream;
                }

                var cache = this.cacheFactory(this.settings.Partition, checkpointer, this.loggerFactory);
                var flowController = new AggregatedQueueFlowController(MaxMessagesPerRead)
                {
                    cache,
                    LoadShedQueueFlowController.CreateAsPercentOfLoadSheddingLimit(this.loadSheddingOptions, environmentStatisticsProvider)
                };

                lock (this.cacheLock)
                {
                    this.cache?.Dispose();
                    this.checkpointer = checkpointer;
                    this.cache = cache;
                    this.flowController = flowController;
                    if (this.recoveredCursorProgress is not null)
                    {
                        this.recoveryCache = cache;
                    }
                }

                this.receiverUsesCheckpoint = receiverUsesCheckpoint;
                this.receiver = this.eventHubReceiverFactory(this.settings, offset, this.logger);
                watch.Stop();
                this.monitor?.TrackInitialization(true, watch.Elapsed, null);
            }
            catch (Exception ex)
            {
                watch.Stop();
                this.monitor?.TrackInitialization(false, watch.Elapsed, ex);
                throw;
            }
        }

        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            => GetQueueMessagesAsync(maxCount, CancellationToken.None);

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(
            int maxCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.receiverState == ReceiverShutdown || maxCount <= 0)
            {
                return new List<IBatchContainer>();
            }

            // if receiver initialization failed, retry
            if (this.receiver == null)
            {
                LogWarningRetryingInitializationOfEventHubPartition(this.settings.Hub.EventHubName, this.settings.Partition);
                await Initialize(cancellationToken);
                if (this.receiver == null)
                {
                    // should not get here, should throw instead, but just incase.
                    return new List<IBatchContainer>();
                }
            }
            var watch = Stopwatch.StartNew();
            List<EventData>? messages;
            try
            {

                // Receivers built against older Orleans versions can still return null.
                messages = (await this.receiver.ReceiveAsync(
                    maxCount,
                    ReceiveTimeout,
                    cancellationToken))?.ToList();
                watch.Stop();

                this.monitor?.TrackRead(true, watch.Elapsed, null);
            }
            catch (Exception ex)
            {
                watch.Stop();
                this.monitor?.TrackRead(false, watch.Elapsed, ex);
                LogWarningFailedToReadFromEventHubPartition(this.settings.Hub.EventHubName, this.settings.Partition, ex);

                if (this.receiverUsesCheckpoint && IsInvalidOffsetException(ex))
                {
                    try
                    {
                        await ResetReceiver(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception recoveryException)
                    {
                        LogWarningFailedToRecoverFromInvalidCheckpoint(
                            this.settings.Hub.EventHubName,
                            this.settings.Partition,
                            recoveryException);
                    }
                }
                throw;
            }

            var batches = new List<IBatchContainer>();
            if (messages is null || messages.Count == 0)
            {
                this.monitor?.TrackMessagesReceived(0, null, null);
                return batches;
            }

            // monitor message age
            var dequeueTimeUtc = DateTime.UtcNow;

            DateTime oldestMessageEnqueueTime = messages[0].EnqueuedTime.UtcDateTime;
            DateTime newestMessageEnqueueTime = messages[messages.Count - 1].EnqueuedTime.UtcDateTime;

            this.monitor?.TrackMessagesReceived(messages.Count, oldestMessageEnqueueTime, newestMessageEnqueueTime);

            List<StreamPosition> messageStreamPositions;
            lock (this.cacheLock)
            {
                if (this.cache is null)
                {
                    return batches;
                }

                messageStreamPositions = this.cache.Add(messages, dequeueTimeUtc);
            }

            foreach (var streamPosition in messageStreamPositions)
            {
                batches.Add(new StreamActivityNotificationBatch(streamPosition));
            }
            return batches;
        }

        private static bool IsInvalidOffsetException(Exception exception)
            => exception is ArgumentException
            && exception.Message.StartsWith("The supplied offset", StringComparison.OrdinalIgnoreCase)
            && exception.Message.Contains(" is invalid.", StringComparison.OrdinalIgnoreCase);

        private async Task ResetReceiver(CancellationToken cancellationToken)
        {
            IStreamQueueCheckpointer<string> checkpointer;
            lock (this.cacheLock)
            {
                checkpointer = this.checkpointer!;
                this.recoveryCache = null;
                this.recoveredCursorProgress = new Dictionary<Cursor, RecoveredCursorProgress>(ReferenceEqualityComparer.Instance);
                this.recoveryPendingCursors = new HashSet<Cursor>(this.cursors, ReferenceEqualityComparer.Instance);
            }

            try
            {
                await checkpointer.Reset(cancellationToken);
            }
            catch
            {
                lock (this.cacheLock)
                {
                    this.recoveryCache = null;
                    this.recoveredCursorProgress = null;
                    this.recoveryPendingCursors = null;
                }

                throw;
            }

            this.receiverUsesCheckpoint = false;
            var receiver = Interlocked.Exchange(ref this.receiver, null);
            var exceptions = new List<Exception>();
            try
            {
                if (receiver is not null)
                {
                    await receiver.CloseAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }

            try
            {
                await Initialize(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }

            if (exceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
            }

            if (exceptions.Count > 1)
            {
                throw new AggregateException(exceptions);
            }
        }

        public void AddToCache(IList<IBatchContainer> messages)
        {
            // do nothing, we add data directly into cache.  No need for agent involvement
        }

        public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
        {
            purgedItems = null;

            lock (this.cacheLock)
            {
                var cache = this.cache;
                if (cache is null)
                {
                    return false;
                }

                //if not under pressure, signal the cache to do a time based purge
                //if under pressure, which means consuming speed is less than producing speed, then shouldn't purge, and don't read more message into the cache
                if (!this.IsUnderPressure())
                {
                    cache.SignalPurge();
                }
            }

            return false;
        }

        [Obsolete("Use IQueueCache.TryGetCacheCursor instead.")]
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            return new Cursor(this, streamId, token);
        }

        QueueCacheCursorResult<IQueueCacheCursor> IQueueCache.TryGetCacheCursor(
            StreamId streamId,
            StreamSequenceToken? token)
        {
            lock (this.cacheLock)
            {
                var cache = this.cache!;
                return WrapCursorResult(
                    streamId,
                    cache,
                    cache.TryGetCursor(streamId, token),
                    ReferenceEquals(cache, this.recoveryCache) ? token : null);
            }
        }

        QueueCacheCursorResult<IQueueCacheCursor> IQueueCache.TryGetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
        {
            lock (this.cacheLock)
            {
                var cache = this.cache!;
                return WrapCursorResult(
                    streamId,
                    cache,
                    cache.TryGetCursorAtPosition(streamId, startPosition),
                    resumeToken: null);
            }
        }

        private QueueCacheCursorResult<IQueueCacheCursor> WrapCursorResult(
            StreamId streamId,
            IEventHubQueueCache cache,
            QueueCacheCursorResult<object> result,
            StreamSequenceToken? resumeToken)
            => result.Kind switch
            {
                QueueCacheCursorResultKind.Success => QueueCacheCursorResult<IQueueCacheCursor>.FromCursor(
                    new Cursor(this, streamId, cache, result.Cursor!, resumeToken)),
                QueueCacheCursorResultKind.CacheMiss => QueueCacheCursorResult<IQueueCacheCursor>.FromCacheMiss(result.CacheMiss!.Value),
                QueueCacheCursorResultKind.NotSupported => QueueCacheCursorResult<IQueueCacheCursor>.NotSupported,
                _ => throw new InvalidOperationException("The cursor result is not initialized."),
            };

        public bool IsUnderPressure()
        {
            return this.GetMaxAddCount() <= 0;
        }

        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            return Task.CompletedTask;
        }

        public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        {
            if (earliestSubscriptionToken is IEventHubPartitionLocation location
                && long.TryParse(location.EventHubOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                IStreamQueueCheckpointer<string>? checkpointer;
                lock (this.cacheLock)
                {
                    if (this.recoveredCursorProgress is { } recoveredProgress)
                    {
                        if (this.recoveryPendingCursors is { Count: > 0 }
                            || !recoveredProgress.Values.Any(
                                progress => ReferenceEquals(progress.DeliveredToken, earliestSubscriptionToken)
                                    || ReferenceEquals(progress.ResumeToken, earliestSubscriptionToken)))
                        {
                            return;
                        }

                        this.recoveryCache = null;
                        this.recoveredCursorProgress = null;
                        this.recoveryPendingCursors = null;
                    }

                    checkpointer = this.checkpointer;
                }

                checkpointer?.Update(location.EventHubOffset, utcNow, CancellationToken.None);
            }
        }

        private void RecordRecoveredDeliveryToken(
            Cursor cursor,
            IEventHubQueueCache? cursorCache,
            StreamSequenceToken deliveredToken,
            StreamSequenceToken? resumeToken)
        {
            if (this.recoveredCursorProgress is not { } cursorProgress
                || !ReferenceEquals(cursorCache, this.recoveryCache))
            {
                return;
            }

            cursorProgress.TryGetValue(cursor, out var previousProgress);
            cursorProgress[cursor] = new(
                deliveredToken,
                previousProgress.ResumeToken ?? resumeToken);
            this.recoveryPendingCursors?.Remove(cursor);
        }

        private void ClearRecoveredDeliveryToken(Cursor cursor)
        {
            this.recoveredCursorProgress?.Remove(cursor);
        }

        private readonly record struct RecoveredCursorProgress(
            StreamSequenceToken DeliveredToken,
            StreamSequenceToken? ResumeToken);

        private void UnregisterCursor(Cursor cursor)
        {
            lock (this.cacheLock)
            {
                this.ClearRecoveredDeliveryToken(cursor);
                this.recoveryPendingCursors?.Remove(cursor);
                this.cursors.Remove(cursor);
            }
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                // if receiver was already shutdown, do nothing
                if (ReceiverShutdown == Interlocked.Exchange(ref this.receiverState, ReceiverShutdown))
                {
                    return;
                }

                LogInfoStoppingReadingFromEventHubPartition(this.settings.Hub.EventHubName, this.settings.Partition);

                var shutdownExceptions = new List<Exception>();

                try
                {
                    // Flush the checkpoint before disposing the cache or closing the receiver,
                    // so the latest processed offset is persisted and not replayed on restart.
                    if (this.checkpointer != null)
                    {
                        using var flushCancellation = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
                        await this.checkpointer.FlushAsync(flushCancellation?.Token ?? CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    shutdownExceptions.Add(ex);
                }

                // clear receiver
                var localReceiver = Interlocked.Exchange(ref this.receiver, null);

                // start closing receiver
                Task closeTask = Task.CompletedTask;
                using var closeCancellation = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
                var closeCancellationToken = closeCancellation?.Token ?? CancellationToken.None;
                try
                {
                    if (localReceiver != null)
                    {
                        closeTask = localReceiver.CloseAsync(closeCancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    shutdownExceptions.Add(ex);
                }

                // dispose of cache
                try
                {
                    lock (this.cacheLock)
                    {
                        var localCache = this.cache;
                        this.cache = null;
                        localCache?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    shutdownExceptions.Add(ex);
                }

                // finish return receiver closing task
                try
                {
                    await closeTask.WaitAsync(closeCancellationToken);
                }
                catch (Exception ex)
                {
                    shutdownExceptions.Add(ex);
                }

                ThrowIfAny(shutdownExceptions);

                watch.Stop();
                this.monitor?.TrackShutdown(true, watch.Elapsed, null);
            }
            catch (Exception ex)
            {
                watch.Stop();
                this.monitor?.TrackShutdown(false, watch.Elapsed, ex);
                throw;
            }

            static void ThrowIfAny(List<Exception> exceptions)
            {
                if (exceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
                }

                if (exceptions.Count > 1)
                {
                    throw new AggregateException(exceptions);
                }
            }
        }

        private static IEventHubReceiver CreateReceiver(EventHubPartitionSettings partitionSettings, string offset, ILogger logger)
        {
            return new EventHubReceiverProxy(partitionSettings, offset, logger);
        }

        /// <summary>
        /// For test purpose. ConfigureDataGeneratorForStream will configure a data generator for the stream
        /// </summary>
        /// <param name="streamId"></param>
        internal void ConfigureDataGeneratorForStream(StreamId streamId)
        {
            (this.receiver as EventHubPartitionGeneratorReceiver)?.ConfigureDataGeneratorForStream(streamId);
        }

        internal void StopProducingOnStream(StreamId streamId)
        {
            (this.receiver as EventHubPartitionGeneratorReceiver)?.StopProducingOnStream(streamId);
        }

        [GenerateSerializer]
        internal class StreamActivityNotificationBatch : IBatchContainer
        {
            [Id(0)]
            public StreamPosition Position { get; }

            public StreamId StreamId => this.Position.StreamId;
            public StreamSequenceToken SequenceToken => this.Position.SequenceToken;

            public StreamActivityNotificationBatch(StreamPosition position)
            {
                this.Position = position;
            }

            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() { throw new NotSupportedException(); }
            public bool ImportRequestContext() { throw new NotSupportedException(); }
        }

        private class Cursor : IQueueCacheCursor
        {
            private readonly EventHubAdapterReceiver owner;
            private readonly StreamId streamId;
            private IEventHubQueueCache? cache;
            private object? cursor;
            private IBatchContainer? current;
            private StreamSequenceToken? recoveryResumeToken;
            private QueueCacheCursorMoveResult? pendingMoveResult;

            public Cursor(EventHubAdapterReceiver owner, StreamId streamId, StreamSequenceToken? token)
            {
                this.owner = owner;
                this.streamId = streamId;
                lock (owner.cacheLock)
                {
                    this.cache = owner.cache!;
#pragma warning disable CS0618 // Preserve the exact legacy exception and cursor behavior.
                    this.cursor = this.cache.GetCursor(streamId, token);
#pragma warning restore CS0618
                    if (ReferenceEquals(this.cache, owner.recoveryCache))
                    {
                        this.recoveryResumeToken = token;
                    }

                    owner.cursors.Add(this);
                }
            }

            public Cursor(
                EventHubAdapterReceiver owner,
                StreamId streamId,
                IEventHubQueueCache cache,
                object cursor,
                StreamSequenceToken? resumeToken)
            {
                this.owner = owner;
                this.streamId = streamId;
                this.cache = cache;
                this.cursor = cursor;
                this.recoveryResumeToken = resumeToken;
                owner.cursors.Add(this);
            }

            public void Dispose()
            {
                this.owner.UnregisterCursor(this);
            }

            public IBatchContainer? GetCurrent(out Exception? exception)
            {
                exception = null;
                return this.current;
            }

            [Obsolete("Use MoveNextWithResult instead.")]
            public bool MoveNext()
            {
                lock (this.owner.cacheLock)
                {
                    if (this.pendingMoveResult is { } pendingResult)
                    {
                        this.pendingMoveResult = null;
                        this.current = null;
                        if (pendingResult.CacheMiss is { } cacheMiss)
                        {
                            Invalidate();
                            throw cacheMiss.ToException();
                        }

                        if (pendingResult.Kind == QueueCacheCursorMoveResultKind.NoData)
                        {
                            return false;
                        }

                        throw new InvalidOperationException("The cursor move result is not initialized.");
                    }

                    if (this.cache is null || this.cursor is null || !ReferenceEquals(this.cache, this.owner.cache))
                    {
                        this.current = null;
                        return false;
                    }

                    try
                    {
#pragma warning disable CS0618 // Preserve the exact legacy exception and cursor behavior.
                        if (!this.cache.TryGetNextMessage(this.cursor, out var next))
#pragma warning restore CS0618
                        {
                            this.current = null;
                            return false;
                        }

                        SetCurrent(next);
                        return true;
                    }
                    catch
                    {
                        Invalidate();
                        throw;
                    }
                }
            }

            public QueueCacheCursorMoveResult MoveNextWithResult()
            {
                lock (this.owner.cacheLock)
                {
                    if (this.pendingMoveResult is { } pendingResult)
                    {
                        this.pendingMoveResult = null;
                        this.current = null;
                        if (pendingResult.Kind == QueueCacheCursorMoveResultKind.CacheMiss)
                        {
                            Invalidate();
                        }

                        return pendingResult;
                    }

                    if (this.cache is null || this.cursor is null || !ReferenceEquals(this.cache, this.owner.cache))
                    {
                        this.current = null;
                        return QueueCacheCursorMoveResult.NoData;
                    }

                    QueueCacheCursorMoveResult result;
                    IBatchContainer? next;
                    try
                    {
                        result = this.cache.TryGetNextMessageWithResult(this.cursor, out next);
                    }
                    catch
                    {
                        Invalidate();
                        throw;
                    }

                    switch (result.Kind)
                    {
                        case QueueCacheCursorMoveResultKind.Success:
                            SetCurrent(next ?? throw new InvalidOperationException(
                                "A successful cursor move did not produce a current item."));
                            break;
                        case QueueCacheCursorMoveResultKind.CacheMiss:
                            this.current = null;
                            Invalidate();
                            break;
                        case QueueCacheCursorMoveResultKind.NoData:
                            this.current = null;
                            break;
                        default:
                            throw new InvalidOperationException("The cursor move result is not initialized.");
                    }

                    return result;
                }
            }

            public void Refresh(StreamSequenceToken token)
            {
                lock (this.owner.cacheLock)
                {
                    var cache = this.owner.cache;
                    if (cache is null)
                    {
                        this.owner.ClearRecoveredDeliveryToken(this);
                        this.cache = null;
                        this.cursor = null;
                        this.current = null;
                        this.recoveryResumeToken = null;
                        this.pendingMoveResult = null;
                        return;
                    }

                    if (!ReferenceEquals(this.cache, cache))
                    {
                        this.owner.ClearRecoveredDeliveryToken(this);
                        this.cache = cache;
                        this.current = null;
                        this.recoveryResumeToken = null;
                        var result = cache.TryGetCursor(this.streamId, token);
                        switch (result.Kind)
                        {
                            case QueueCacheCursorResultKind.Success:
                                this.cursor = result.Cursor;
                                this.pendingMoveResult = null;
                                break;
                            case QueueCacheCursorResultKind.CacheMiss:
                                this.cursor = null;
                                this.pendingMoveResult = QueueCacheCursorMoveResult.FromCacheMiss(result.CacheMiss!.Value);
                                break;
                            default:
                                throw new InvalidOperationException("The cursor result is not initialized.");
                        }

                        return;
                    }

                    cache.Refresh(this.cursor!, token);
                }
            }

            public void RecordDeliveryFailure()
            {
            }

            private void SetCurrent(IBatchContainer next)
            {
                this.current = next;
                this.owner.RecordRecoveredDeliveryToken(
                    this,
                    this.cache,
                    next.SequenceToken,
                    this.recoveryResumeToken);
                this.recoveryResumeToken = null;
            }

            private void Invalidate()
            {
                this.owner.UnregisterCursor(this);
                this.cache = null;
                this.cursor = null;
                this.current = null;
                this.recoveryResumeToken = null;
                this.pendingMoveResult = null;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Initializing EventHub partition {EventHubName}-{Partition}."
        )]
        private partial void LogInfoInitializingEventHubPartition(string eventHubName, string partition);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Stopping reading from EventHub partition {EventHubName}-{Partition}"
        )]
        private partial void LogInfoStoppingReadingFromEventHubPartition(string eventHubName, string partition);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)OrleansEventHubErrorCode.RetryReceiverInit,
            Message = "Retrying initialization of EventHub partition {EventHubName}-{Partition}."
        )]
        private partial void LogWarningRetryingInitializationOfEventHubPartition(string eventHubName, string partition);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)OrleansEventHubErrorCode.FailedPartitionRead,
            Message = "Failed to read from EventHub partition {EventHubName}-{Partition}"
        )]
        private partial void LogWarningFailedToReadFromEventHubPartition(string eventHubName, string partition, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)OrleansEventHubErrorCode.RetryReceiverInit,
            Message = "Failed to recover EventHub partition {EventHubName}-{Partition} from an invalid checkpoint. The original read failure will be rethrown."
        )]
        private partial void LogWarningFailedToRecoverFromInvalidCheckpoint(string eventHubName, string partition, Exception exception);
    }
}
