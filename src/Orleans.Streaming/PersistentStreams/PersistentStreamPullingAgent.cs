using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using StreamingEvents = Orleans.Streaming.Diagnostics.StreamingEvents;
using Orleans.Streams.Filtering;

#nullable disable
namespace Orleans.Streams
{
    internal sealed partial class PersistentStreamPullingAgent : SystemTarget, IPersistentStreamPullingAgent, PersistentStreamPullingAgent.ITestAccessor
    {
        private const int ReadLoopRetryMax = 6;
        private const int StreamInactivityCheckFrequency = 10;
        private readonly IBackoffProvider deliveryBackoffProvider;
        private readonly IBackoffProvider queueReaderBackoffProvider;
        private readonly string streamProviderName;
        private readonly IStreamPubSub pubSub;
        private readonly IStreamFilter streamFilter;
        private readonly Dictionary<QualifiedStreamId, StreamConsumerCollection> pubSubCache;
        private readonly StreamPullingAgentOptions options;
        private readonly ILogger logger;
        private readonly IQueueAdapterCache queueAdapterCache;
        private readonly IQueueAdapter queueAdapter;
        private readonly IStreamFailureHandler streamFailureHandler;
        private readonly TimeProvider _timeProvider;
        internal readonly QueueId QueueId;

        private int numMessages;
        private IQueueCache queueCache;
        private IQueueAdapterReceiver receiver;
        private DateTime lastTimeCleanedPubSubCache;
        private IGrainTimer timer;

        private Task receiverInitTask;
        private Task _activePumpTask = Task.CompletedTask;
        private bool _isStopping;
        private bool IsShutdown => _isStopping;
        private string StatisticUniquePostfix => $"{streamProviderName}.{QueueId}";

        internal interface ITestAccessor
        {
            Task<bool> ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver receiver, int maxCacheAddCount);
            Task RegisterStream(QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now);
            Task<IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>> GetPubSubCache();
            Task RunQueuePump(QueueId myQueueId, CancellationToken cancellationToken);
            Task Shutdown();
        }

        internal PersistentStreamPullingAgent(
            SystemTargetGrainId id,
            string strProviderName,
            IStreamPubSub streamPubSub,
            IStreamFilter streamFilter,
            QueueId queueId,
            StreamPullingAgentOptions options,
            IQueueAdapter queueAdapter,
            IQueueAdapterCache queueAdapterCache,
            IStreamFailureHandler streamFailureHandler,
            IBackoffProvider deliveryBackoffProvider,
            IBackoffProvider queueReaderBackoffProvider,
            TimeProvider timeProvider,
            SystemTargetShared shared)
            : base(id, shared)
        {
            if (strProviderName == null) throw new ArgumentNullException("runtime", "PersistentStreamPullingAgent: strProviderName should not be null");

            QueueId = queueId;
            streamProviderName = strProviderName;
            pubSub = streamPubSub;
            this.streamFilter = streamFilter;
            pubSubCache = new Dictionary<QualifiedStreamId, StreamConsumerCollection>();
            this.options = options;
            this.queueAdapter = queueAdapter ?? throw new ArgumentNullException(nameof(queueAdapter));
            this.streamFailureHandler = streamFailureHandler ?? throw new ArgumentNullException(nameof(streamFailureHandler));
            this.queueAdapterCache = queueAdapterCache;
            this.deliveryBackoffProvider = deliveryBackoffProvider;
            this.queueReaderBackoffProvider = queueReaderBackoffProvider;
            _timeProvider = timeProvider ?? TimeProvider.System;
            numMessages = 0;

            logger = shared.LoggerFactory.CreateLogger($"{this.GetType().Namespace}.{streamProviderName}");
            LogInfoCreated(GetType().Name, GrainId, strProviderName, Silo, new(QueueId));
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        Task<bool> ITestAccessor.ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver receiver, int maxCacheAddCount)
            => this.RunOrQueueTaskResult(() => ReadFromQueue(myQueueId, receiver, maxCacheAddCount, CancellationToken.None)).Unwrap();

        Task ITestAccessor.RegisterStream(QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now)
            => this.RunOrQueueTaskResult(() =>
            {
                RegisterStream(streamId, firstToken, now);

                if (pubSubCache.TryGetValue(streamId, out var streamData) && streamData.RegistrationTask is { } registrationTask)
                {
                    return registrationTask;
                }

                return Task.CompletedTask;
            }).Unwrap();

        Task<IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>> ITestAccessor.GetPubSubCache()
            => this.RunOrQueueTaskResult(() => (IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>)new Dictionary<QualifiedStreamId, StreamConsumerCollection>(pubSubCache));

        Task ITestAccessor.RunQueuePump(QueueId myQueueId, CancellationToken cancellationToken)
            => this.RunOrQueueTask(() => RunQueuePump(myQueueId, cancellationToken));

        Task ITestAccessor.Shutdown() => this.RunOrQueueTask(() => Shutdown());

        /// <summary>
        /// Take responsibility for a new queues that was assigned to me via a new range.
        /// We first store the new queue in our internal data structure, try to initialize it and start a pumping timer.
        /// ERROR HANDLING:
        ///     The responsibility to handle initialization and shutdown failures is inside the INewQueueAdapterReceiver code.
        ///     The agent will call Initialize once and log an error. It will not call initialize again.
        ///     The receiver itself may attempt later to recover from this error and do initialization again.
        ///     The agent will assume initialization has succeeded and will subsequently start calling pumping receive.
        ///     Same applies to shutdown.
        /// </summary>
        /// <returns></returns>
        public Task Initialize()
        {
            LogInfoInit(GetType().Name, GrainId, Silo, new(QueueId));

            _isStopping = false;
            _activePumpTask = Task.CompletedTask;
            lastTimeCleanedPubSubCache = _timeProvider.GetUtcNow().UtcDateTime;

            try
            {
                if (queueAdapterCache != null)
                {
                    using var _ = new ExecutionContextSuppressor();
                    queueCache = queueAdapterCache.CreateQueueCache(QueueId);
                }
            }
            catch (Exception exc)
            {
                LogErrorCreatingQueueCache(exc);
                throw;
            }

            try
            {
                using var _ = new ExecutionContextSuppressor();
                receiver = queueAdapter.CreateReceiver(QueueId);
            }
            catch (Exception exc)
            {
                LogErrorCreatingReceiver(exc);
                throw;
            }

            try
            {
                using var _ = new ExecutionContextSuppressor();
                receiverInitTask = OrleansTaskExtentions.SafeExecute(() => receiver.Initialize(this.options.InitQueueTimeout))
                    .LogException(logger, ErrorCode.PersistentStreamPullingAgent_03, $"QueueAdapterReceiver {QueueId:H} failed to Initialize.");
                receiverInitTask.Ignore();
            }
            catch (Exception exception)
            {
                LogErrorReceiverInit(new(QueueId), exception);

                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Initialize. No need to log again.
            }

            // Setup a reader for a new receiver.
            // Even if the receiver failed to initialize, treat it as OK and start pumping it. It's receiver responsibility to retry initialization.
            var randomTimerOffset = RandomTimeSpan.Next(this.options.GetQueueMsgsTimerPeriod);
            timer = RegisterGrainTimer(RunQueuePump, QueueId, randomTimerOffset, this.options.GetQueueMsgsTimerPeriod);

            StreamInstruments.RegisterPersistentStreamPubSubCacheSizeObserve(() => new Measurement<int>(pubSubCache.Count, new KeyValuePair<string, object>("name", StatisticUniquePostfix)));

            LogInfoTakingQueue(new(QueueId));
            return Task.CompletedTask;
        }

        public async Task Shutdown()
        {
            // Stop pulling from queues that are not in my range anymore.
            LogInfoShutdown(GetType().Name, new(QueueId));

            _isStopping = true;
            var asyncTimer = timer;
            timer = null;
            asyncTimer?.Dispose();

            Task localReceiverInitTask = receiverInitTask;
            if (localReceiverInitTask != null)
            {
                await localReceiverInitTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                receiverInitTask = null;
            }

            await _activePumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

            this.queueCache = null;

            try
            {
                IQueueAdapterReceiver localReceiver = this.receiver;
                this.receiver = null;
                if (localReceiver != null)
                {
                    var task = OrleansTaskExtentions.SafeExecute(() => localReceiver.Shutdown(this.options.InitQueueTimeout));
                    task = task.LogException(logger, ErrorCode.PersistentStreamPullingAgent_07,
                        $"QueueAdapterReceiver {QueueId} failed to Shutdown.");
                    await task;
                }
            }
            catch
            {
                // Just ignore this exception and proceed as if Shutdown has succeeded.
                // We already logged individual exceptions for individual calls to Shutdown. No need to log again.
            }

            // Drain any in-progress background registration tasks before proceeding.
            // Setting _isStopping = true above makes IsShutdown = true, which causes registrations
            // to stop retrying, so these tasks will complete quickly.
            var inFlightRegistrations = pubSubCache.Values
                .Select(v => v.RegistrationTask)
                .Where(t => t is not null)
                .ToList();
            if (inFlightRegistrations.Count > 0)
            {
                await Task.WhenAll(inFlightRegistrations)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            var unregisterTasks = new List<Task>();
            foreach (var tuple in pubSubCache)
            {
                tuple.Value.DisposeAll(logger);
                var streamId = tuple.Key;
                LogInfoUnregisterProducer(streamId);
                unregisterTasks.Add(pubSub.UnregisterProducer(streamId, GrainId));
            }

            try
            {
                await Task.WhenAll(unregisterTasks);
            }
            catch (Exception exc)
            {
                LogWarningUnregisterProducer(exc);
            }
            pubSubCache.Clear();
        }

        public Task AddSubscriber(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            GrainId streamConsumer,
            string filterData)
        {
            LogDebugAddSubscriber(streamId, streamConsumer);
            // cannot await here because explicit consumers trigger this call, so it could cause a deadlock.
            AddSubscriber_Impl(subscriptionId, streamId, streamConsumer, filterData, null)
                .LogException(logger, ErrorCode.PersistentStreamPullingAgent_26,
                    $"Failed to add subscription for stream {streamId}.")
                .Ignore();
            return Task.CompletedTask;
        }

        // Called by rendezvous when new remote subscriber subscribes to this stream.
        private async Task AddSubscriber_Impl(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            GrainId streamConsumer,
            string filterData,
            StreamSequenceToken cacheToken)
        {
            if (IsShutdown) return;

            StreamConsumerCollection streamDataCollection;
            if (!pubSubCache.TryGetValue(streamId, out streamDataCollection))
            {
                // If stream is not in pubsub cache, then we've received no events on this stream, and will aquire the subscriptions from pubsub when we do.
                return;
            }

            StreamConsumerData data;
            if (!streamDataCollection.TryGetConsumer(subscriptionId, out data))
            {
                var consumerReference = this.RuntimeClient.InternalGrainFactory
                    .GetGrain(streamConsumer)
                    .AsReference<IStreamConsumerExtension>();
                data = streamDataCollection.AddConsumer(subscriptionId, streamId, consumerReference, filterData, _timeProvider.GetUtcNow().UtcDateTime);
                StreamingEvents.EmitSubscriptionAdded(streamProviderName, streamId.StreamId, subscriptionId.Guid, streamConsumer, Silo);
            }

            if (await DoHandshakeWithConsumer(data, cacheToken))
            {
                data.PendingStartToken = null;
                data.IsRegistered = true;
                StreamingEvents.EmitSubscriptionAttached(streamProviderName, streamId.StreamId, subscriptionId.Guid, streamConsumer, Silo);
                if (data.State == StreamConsumerDataState.Inactive)
                    RunConsumerCursor(data).Ignore(); // Start delivering events if not actively doing so
            }
        }

        private async Task<bool> DoHandshakeWithConsumer(
            StreamConsumerData consumerData,
            StreamSequenceToken cacheToken)
        {
            if (IsShutdown) return false;

            StreamHandshakeToken requestedHandshakeToken = null;
            // if not cache, then we can't get cursor and there is no reason to ask consumer for token.
            if (queueCache != null)
            {
                Exception exceptionOccured = null;
                try
                {
                    requestedHandshakeToken = await AsyncExecutorWithRetries.ExecuteWithRetries(
                         i => consumerData.StreamConsumer.GetSequenceToken(consumerData.SubscriptionId),
                         AsyncExecutorWithRetries.INFINITE_RETRIES,
                         // Do not retry if the agent is shutting down, or if the exception is ClientNotAvailableException
                         (exception, i) => exception is not ClientNotAvailableException && !IsShutdown,
                         this.options.MaxEventDeliveryTime,
                         deliveryBackoffProvider);

                    var requestedToken = requestedHandshakeToken?.Token;
                    if (requestedToken != null)
                    {
                        consumerData.SafeDisposeCursor(logger);
                        consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, requestedToken);
                    }
                    else
                    {
                        var registrationToken = cacheToken ?? consumerData.PendingStartToken;
                        if (consumerData.Cursor == null) // if the consumer did not ask for a specific token and we already have a cursor, just keep using it.
                            consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, registrationToken);
                    }
                }
                catch (Exception exception)
                {
                    exceptionOccured = exception;
                }
                if (exceptionOccured != null)
                {
                    // If we are shutting down, ignore the error
                    if (IsShutdown) return false;

                    bool faultedSubscription = await ErrorProtocol(consumerData, exceptionOccured, false, null, requestedHandshakeToken?.Token);
                    if (faultedSubscription) return false;
                }
            }
            consumerData.LastToken = requestedHandshakeToken; // use what ever the consumer asked for as LastToken for next handshake (even if he asked for null).
            // if we don't yet have a cursor (had errors in the handshake or data not available exc), get a cursor at the event that triggered that consumer subscription.
            if (consumerData.Cursor == null && queueCache != null)
            {
                try
                {
                    var registrationToken = cacheToken ?? consumerData.PendingStartToken;
                    consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, registrationToken);
                }
                catch (Exception)
                {
                    consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, null); // just in case last GetCacheCursor failed.
                }
            }
            return true;
        }

        public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            RemoveSubscriber_Impl(subscriptionId, streamId);
            return Task.CompletedTask;
        }

        public void RemoveSubscriber_Impl(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            if (IsShutdown) return;

            StreamConsumerCollection streamData;
            if (!pubSubCache.TryGetValue(streamId, out streamData)) return;

            // remove consumer
            bool removed = streamData.RemoveConsumer(subscriptionId, logger);
            if (removed)
            {
                StreamingEvents.EmitSubscriptionDetached(streamProviderName, streamId.StreamId, subscriptionId.Guid, Silo);
                StreamingEvents.EmitSubscriptionRemoved(streamProviderName, streamId.StreamId, subscriptionId.Guid, Silo);
                LogDebugRemovedConsumer(subscriptionId, streamId);
            }

            if (streamData.Count == 0)
                pubSubCache.Remove(streamId);
        }

        private Task RunQueuePump(QueueId queueId, CancellationToken cancellationToken)
        {
            using var _ = new ExecutionContextSuppressor();
            if (IsShutdown)
            {
                return Task.CompletedTask;
            }

            if (!_activePumpTask.IsCompleted)
            {
                return _activePumpTask;
            }

            return _activePumpTask = PumpQueue(queueId, cancellationToken);
        }

        private async Task PumpQueue(QueueId queueId, CancellationToken cancellationToken)
        {
            try
            {
                Task localReceiverInitTask = receiverInitTask;
                if (localReceiverInitTask != null)
                {
                    await localReceiverInitTask;
                    receiverInitTask = null;
                }

                if (IsShutdown || cancellationToken.IsCancellationRequested) return; // timer was already removed, last tick

                // loop through the queue until it is empty.
                while (!IsShutdown && !cancellationToken.IsCancellationRequested) // shutdown sets IsShutdown and cancels the timer token.
                {
                    int maxCacheAddCount = queueCache?.GetMaxAddCount() ?? QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG;
                    if (maxCacheAddCount != QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG && maxCacheAddCount <= 0)
                        return;

                    // If read succeeds and there is more data, we continue reading.
                    // If read succeeds and there is no more data, we break out of loop
                    // If read fails, we retry 6 more times, with backoff policy.
                    //    we log each failure as warnings. After 6 times retry if still fail, we break out of loop and log an error
                    bool moreData = await AsyncExecutorWithRetries.ExecuteWithRetries(
                        i => ReadFromQueue(queueId, receiver, maxCacheAddCount, cancellationToken),
                        ReadLoopRetryMax,
                        ReadLoopRetryExceptionFilter,
                        Timeout.InfiniteTimeSpan,
                        queueReaderBackoffProvider,
                        cancellationToken: cancellationToken);
                    if (!moreData)
                        return;
                }
            }
            catch (Exception exc)
            {
                receiverInitTask = null;
                LogErrorGivingUpReading(new(queueId), ReadLoopRetryMax, exc);
            }

            bool ReadLoopRetryExceptionFilter(Exception e, int retryCounter)
            {
                LogErrorRetrying(retryCounter, new(queueId), e);
                return !cancellationToken.IsCancellationRequested && !IsShutdown;
            }
        }

        /// <summary>
        /// Read from queue.
        /// Returns true, if data was read, false if it was not
        /// </summary>
        /// <param name="myQueueId"></param>
        /// <param name="rcvr"></param>
        /// <param name="maxCacheAddCount"></param>
        /// <returns></returns>
        private async Task<bool> ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver rcvr, int maxCacheAddCount, CancellationToken cancellationToken)
        {
            if (rcvr == null || IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            // Try to cleanup the pubsub cache at the cadence of 10 times in the configurable StreamInactivityPeriod.
            if ((now - lastTimeCleanedPubSubCache) >= this.options.StreamInactivityPeriod.Divide(StreamInactivityCheckFrequency))
            {
                lastTimeCleanedPubSubCache = now;
                CleanupPubSubCache(now);
            }

            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (queueCache != null)
            {
                IList<IBatchContainer> purgedItems;
                if (queueCache.TryPurgeFromCache(out purgedItems))
                {
                    try
                    {
                        await rcvr.MessagesDeliveredAsync(purgedItems);
                    }
                    catch (Exception exc)
                    {
                        LogWarningMessagesDeliveredAsync(new(myQueueId), exc);
                    }
                }
            }

            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (queueCache != null && queueCache.IsUnderPressure())
            {
                // Under back pressure. Exit the loop. Will attempt again in the next timer callback.
                LogInfoStreamCacheUnderPressure();
                return false;
            }

            // Retrieve one multiBatch from the queue. Every multiBatch has an IEnumerable of IBatchContainers, each IBatchContainer may have multiple events.
            IList<IBatchContainer> multiBatch = await rcvr.GetQueueMessagesAsync(maxCacheAddCount);

            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (multiBatch == null || multiBatch.Count == 0) return false; // queue is empty. Exit the loop. Will attempt again in the next timer callback.

            queueCache?.AddToCache(multiBatch);
            numMessages += multiBatch.Count;
            StreamInstruments.PersistentStreamReadMessages.Add(multiBatch.Count);

            LogTraceGotMessages(multiBatch.Count, new(myQueueId), numMessages);

            foreach (var group in
                multiBatch
                .Where(m => m != null)
                .GroupBy(container => container.StreamId))
            {
                if (IsShutdown || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                var streamId = new QualifiedStreamId(queueAdapter.Name, group.Key);
                StreamSequenceToken startToken = group.First().SequenceToken;
                StreamConsumerCollection streamData;
                if (pubSubCache.TryGetValue(streamId, out streamData))
                {
                    streamData.RefreshActivity(now);
                    StartInactiveCursors(streamData, startToken);
                }
                else
                {
                    // Run registration in the background so that cold-stream pubsub
                    // calls do not stall message delivery for other streams on the same queue.
                    RegisterStream(streamId, startToken, now);
                }
            }

            return true;
        }

        private void CleanupPubSubCache(DateTime now)
        {
            foreach (var tuple in pubSubCache)
            {
                if (tuple.Value.IsInactive(now, options.StreamInactivityPeriod))
                {
                    pubSubCache.Remove(tuple.Key);
                    tuple.Value.DisposeAll(logger);
                    StreamingEvents.EmitStreamInactive(streamProviderName, tuple.Key.StreamId, options.StreamInactivityPeriod, Silo);
                }
            }
        }

        private void RegisterStream(QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now)
        {
            if (IsShutdown)
            {
                return;
            }

            var streamData = new StreamConsumerCollection(now);

            // Create a fake cursor to point into a cache.
            // That way we will not purge the event from the cache, until we talk to pub sub.
            // This will help ensure the "casual consistency" between pre-existing subscripton (of a potentially new already subscribed consumer)
            // and later production.
            var pinCursor = queueCache?.GetCacheCursor(streamId, firstToken);
            streamData.RegistrationTask = RegisterStreamAsync();
            pubSubCache.Add(streamId, streamData);

            async Task RegisterStreamAsync()
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

                if (IsShutdown)
                {
                    return;
                }

                try
                {
                    var subscribers = await RegisterAsStreamProducer(streamId);

                    if (IsShutdown)
                    {
                        return;
                    }

                    // Producer registration succeeded; the stream entry is now established.
                    // Subscriber-handshake failures must not tear down the entry from here on.
                    streamData.StreamRegistered = true;

                    LogDebugGotBackSubscribers(subscribers.Count, streamId);

                    // Await all initial subscriber handshakes before releasing the pin cursor so
                    // that the first batch cannot be purged from the cache before each subscriber
                    // has established its own cursor.
                    if (subscribers.Count > 0)
                    {
                        var addSubscriptionTasks = new List<Task>(subscribers.Count);
                        foreach (PubSubSubscriptionState item in subscribers)
                        {
                            addSubscriptionTasks.Add(SubscribeWithIsolation(item));
                        }

                        await Task.WhenAll(addSubscriptionTasks);
                    }
                }
                catch (Exception exception)
                {
                    FailRegistration(exception);
                }
                finally
                {
                    streamData.RegistrationTask = null;

                    // Disposed after all initial subscriber handshakes complete so the first
                    // batch stays pinned until each subscriber has its own cache cursor.
                    pinCursor?.Dispose();
                }
            }

            void FailRegistration(Exception exception)
            {
                LogWarningFailedToRegisterStream(streamId, exception);

                // Only reached when producer registration itself fails.
                if (pubSubCache.TryGetValue(streamId, out var cachedStreamData)
                    && ReferenceEquals(cachedStreamData, streamData))
                {
                    pubSubCache.Remove(streamId);
                }

                streamData.DisposeAll(logger);
            }

            async Task SubscribeWithIsolation(PubSubSubscriptionState item)
            {
                if (IsShutdown)
                {
                    return;
                }

                try
                {
                    await AddSubscriber_Impl(item.SubscriptionId, item.Stream, item.Consumer, item.FilterData, firstToken);
                }
                catch (Exception exception)
                {
                    LogWarningFailedToAddSubscription(item.Stream, exception);
                }
            }
        }

        private void StartInactiveCursors(StreamConsumerCollection streamData, StreamSequenceToken startToken)
        {
            foreach (StreamConsumerData consumerData in streamData.AllConsumers())
            {
                if (IsShutdown)
                {
                    return;
                }

                // Some consumer might not be fully registered yet
                if (consumerData.IsRegistered)
                {
                    consumerData.Cursor?.Refresh(startToken);
                    if (consumerData.State == StreamConsumerDataState.Inactive)
                    {
                        // wake up inactive consumers
                        RunConsumerCursor(consumerData).Ignore();
                    }
                }
                else
                {
                    if (consumerData.PendingStartToken is null || startToken.Older(consumerData.PendingStartToken))
                    {
                        consumerData.PendingStartToken = startToken;
                    }
                    LogDebugPulledNewMessages(consumerData.StreamId);
                }
            }
        }

        private async Task RunConsumerCursor(StreamConsumerData consumerData)
        {
            try
            {
                // double check in case of interleaving
                if (consumerData.State == StreamConsumerDataState.Active ||
                    consumerData.Cursor == null) return;

                consumerData.State = StreamConsumerDataState.Active;
                var deliveredAny = false;
                while (!IsShutdown && consumerData.Cursor != null)
                {
                    IBatchContainer batch = null;
                    Exception exceptionOccured = null;
                    try
                    {
                        batch = GetBatchForConsumer(consumerData.Cursor, consumerData.StreamId, consumerData.FilterData);
                        if (batch == null)
                        {
                            // Only emit cursor-drained when we transitioned from delivering to empty,
                            // not on every empty poll.
                            if (deliveredAny)
                                StreamingEvents.EmitConsumerCursorDrained(streamProviderName, consumerData.StreamId.StreamId, consumerData.SubscriptionId.Guid, Silo);
                            break;
                        }
                    }
                    catch (Exception exc)
                    {
                        exceptionOccured = exc;
                        consumerData.SafeDisposeCursor(logger);
                        consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, null);
                    }

                    if (batch != null)
                    {
                        deliveredAny = true;
                        if (!ShouldDeliverBatch(consumerData.StreamId, batch, consumerData.FilterData))
                            continue;
                    }

                    try
                    {
                        StreamInstruments.PersistentStreamSentMessages.Add(1);
                        if (IsShutdown)
                        {
                            break;
                        }

                        if (batch != null)
                        {
                            StreamHandshakeToken newToken = await AsyncExecutorWithRetries.ExecuteWithRetries(
                                i => DeliverBatchToConsumer(consumerData, batch),
                                AsyncExecutorWithRetries.INFINITE_RETRIES,
                                // Do not retry if the agent is shutting down, or if the exception is ClientNotAvailableException
                                (exception, i) => exception is not ClientNotAvailableException && !IsShutdown,
                                this.options.MaxEventDeliveryTime,
                                deliveryBackoffProvider);
                            if (newToken != null)
                            {
                                consumerData.LastToken = newToken;
                                IQueueCacheCursor newCursor = queueCache.GetCacheCursor(consumerData.StreamId, newToken.Token);
                                // The handshake token points to an already processed event, we need to advance the cursor to
                                // the next event.
                                newCursor.MoveNext();
                                consumerData.SafeDisposeCursor(logger);
                                consumerData.Cursor = newCursor;
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        consumerData.Cursor?.RecordDeliveryFailure();
                        LogErrorDeliveringMessages(consumerData.StreamId, exc);

                        exceptionOccured = exc is ClientNotAvailableException
                            ? exc
                            : new StreamEventDeliveryFailureException(consumerData.StreamId);
                    }
                    // if we failed to deliver a batch
                    if (exceptionOccured != null)
                    {
                        bool faultedSubscription = await ErrorProtocol(consumerData, exceptionOccured, true, batch, batch?.SequenceToken);
                        if (faultedSubscription) return;
                    }
                }
                consumerData.State = StreamConsumerDataState.Inactive;
            }
            catch (Exception exc)
            {
                // RunConsumerCursor is fired with .Ignore so we should log if anything goes wrong, because there is no one to catch the exception
                LogErrorRunConsumerCursor(exc);
                consumerData.State = StreamConsumerDataState.Inactive;
                throw;
            }
        }

        private IBatchContainer GetBatchForConsumer(IQueueCacheCursor cursor, StreamId streamId, string filterData)
        {
            if (this.options.BatchContainerBatchSize <= 1)
            {
                if (!cursor.MoveNext())
                {
                    return null;
                }

                return cursor.GetCurrent(out _);
            }
            else if (this.options.BatchContainerBatchSize > 1)
            {
                int i = 0;
                var batchContainers = new List<IBatchContainer>();

                while (i < this.options.BatchContainerBatchSize)
                {
                    if (!cursor.MoveNext())
                    {
                        break;
                    }

                    var batchContainer = cursor.GetCurrent(out _);

                    if (!ShouldDeliverBatch(streamId, batchContainer, filterData))
                        continue;

                    batchContainers.Add(batchContainer);
                    i++;
                }

                if (i == 0)
                {
                    return null;
                }

                return new BatchContainerBatch(batchContainers);
            }

            return null;
        }

        private async Task<StreamHandshakeToken> DeliverBatchToConsumer(StreamConsumerData consumerData, IBatchContainer batch)
        {
            try
            {
                StreamHandshakeToken newToken = await ContextualizedDeliverBatchToConsumer(consumerData, batch);
                consumerData.LastToken = StreamHandshakeToken.CreateDeliveyToken(batch.SequenceToken); // this is the currently delivered token
                StreamingEvents.EmitMessageDelivered(streamProviderName, consumerData, batch, Silo);

                return newToken;
            }
            catch (Exception ex)
            {
                LogWarningFailedToDeliverMessage(consumerData.SubscriptionId, consumerData.StreamId, ex);
                throw;
            }
        }

        /// <summary>
        /// Add call context for batch delivery call, then clear context immediately, without giving up turn.
        /// </summary>
        private static Task<StreamHandshakeToken> ContextualizedDeliverBatchToConsumer(StreamConsumerData consumerData, IBatchContainer batch)
        {
            bool isRequestContextSet = batch.ImportRequestContext();
            try
            {
                return consumerData.StreamConsumer.DeliverBatch(consumerData.SubscriptionId, consumerData.StreamId, batch, consumerData.LastToken);
            }
            finally
            {
                if (isRequestContextSet)
                {
                    // clear RequestContext before await!
                    RequestContext.Clear();
                }
            }
        }


        private static async Task DeliverErrorToConsumer(StreamConsumerData consumerData, Exception exc, IBatchContainer batch)
        {
            Task errorDeliveryTask;
            bool isRequestContextSet = batch != null && batch.ImportRequestContext();
            try
            {
                errorDeliveryTask = consumerData.StreamConsumer.ErrorInStream(consumerData.SubscriptionId, exc);
            }
            finally
            {
                if (isRequestContextSet)
                {
                    RequestContext.Clear(); // clear RequestContext before await!
                }
            }
            await errorDeliveryTask;
        }

        private async Task<bool> ErrorProtocol(StreamConsumerData consumerData, Exception exceptionOccured, bool isDeliveryError, IBatchContainer batch, StreamSequenceToken token)
        {
            // for loss of client, we just remove the subscription
            if (exceptionOccured is ClientNotAvailableException)
            {
                LogWarningConsumerIsDead(consumerData.StreamConsumer, consumerData.StreamId);
                pubSub.UnregisterConsumer(consumerData.SubscriptionId, consumerData.StreamId).Ignore();
                return true;
            }

            // notify consumer about the error or that the data is not available.
            await DeliverErrorToConsumer(consumerData, exceptionOccured, batch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            // record that there was a delivery failure
            if (isDeliveryError)
            {
                await streamFailureHandler.OnDeliveryFailure(
                        consumerData.SubscriptionId, streamProviderName, consumerData.StreamId, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            else
            {
                await streamFailureHandler.OnSubscriptionFailure(
                    consumerData.SubscriptionId, streamProviderName, consumerData.StreamId, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            // if configured to fault on delivery failure and this is not an implicit subscription, fault and remove the subscription
            if (streamFailureHandler.ShouldFaultSubsriptionOnError && !SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
            {
                try
                {
                    // notify consumer of faulted subscription, if we can.
                    await DeliverErrorToConsumer(
                        consumerData, new FaultedSubscriptionException(consumerData.SubscriptionId, consumerData.StreamId), batch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

                    // mark subscription as faulted.
                    await pubSub.FaultSubscription(consumerData.StreamId, consumerData.SubscriptionId);
                }
                finally
                {
                    // remove subscription
                    RemoveSubscriber_Impl(consumerData.SubscriptionId, consumerData.StreamId);
                }
                return true;
            }
            return false;
        }

        private static async Task<ISet<PubSubSubscriptionState>> PubsubRegisterProducer(IStreamPubSub pubSub, QualifiedStreamId streamId,
            GrainId meAsStreamProducer, ILogger logger)
        {
            try
            {
                var streamData = await pubSub.RegisterProducer(streamId, meAsStreamProducer);
                return streamData;
            }
            catch (Exception e)
            {
                LogErrorRegisterAsStreamProducer(logger, e);
                throw;
            }
        }

        private async Task<ISet<PubSubSubscriptionState>> RegisterAsStreamProducer(QualifiedStreamId streamId)
        {
            if (pubSub == null) throw new NullReferenceException("Found pubSub reference not set up correctly in RetrieveNewStream");
            if (IsShutdown)
            {
                return new HashSet<PubSubSubscriptionState>();
            }

            ISet<PubSubSubscriptionState> subscribers = null;
            await AsyncExecutorWithRetries.ExecuteWithRetries(
                async i => { subscribers = await PubsubRegisterProducer(pubSub, streamId, GrainId, logger); },
                AsyncExecutorWithRetries.INFINITE_RETRIES,
                (exception, i) => !IsShutdown,
                Timeout.InfiniteTimeSpan,
                deliveryBackoffProvider);

            return subscribers;
        }

        private bool ShouldDeliverBatch(StreamId streamId, IBatchContainer batchContainer, string filterData)
        {
            if (this.streamFilter is NoOpStreamFilter)
                return true;

            try
            {
                foreach (var evt in batchContainer.GetEvents<object>())
                {
                    if (this.streamFilter.ShouldDeliver(streamId, evt.Item1, filterData))
                        return true;
                }
                return false;
            }
            catch (Exception exc)
            {
                LogWarningFilterEvaluation(streamFilter.GetType().Name, filterData, streamId, exc);
            }
            return true;
        }

        private readonly struct QueueIdLogRecord(QueueId queueId)
        {
            public override string ToString() => queueId.ToStringWithHashCode();
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_01,
            Message = "Created {Name} {Id} for Stream Provider {StreamProvider} on silo {Silo} for Queue {Queue}."
        )]
        private partial void LogInfoCreated(string name, GrainId id, string streamProvider, SiloAddress silo, QueueIdLogRecord queue);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_02,
            Message = "Init of {Name} {Id} on silo {Silo} for queue {Queue}."
        )]
        private partial void LogInfoInit(string name, GrainId id, SiloAddress silo, QueueIdLogRecord queue);

        [LoggerMessage(

            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_23,
            Message = "Exception while calling IQueueAdapterCache.CreateQueueCache."
        )]
        private partial void LogErrorCreatingQueueCache(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_02,
            Message = "Exception while calling IQueueAdapter.CreateNewReceiver."
        )]
        private partial void LogErrorCreatingReceiver(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_03,
            Message = "QueueAdapterReceiver {QueueId} failed to Initialize."
        )]
        private partial void LogErrorReceiverInit(QueueIdLogRecord queueId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_04,
            Message = "Taking queue {Queue} under my responsibility."
        )]
        private partial void LogInfoTakingQueue(QueueIdLogRecord queue);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_05,
            Message = "Shutdown of {Name} responsible for queue: {Queue}"
        )]
        private partial void LogInfoShutdown(string name, QueueIdLogRecord queue);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_06,
            Message = "Unregister PersistentStreamPullingAgent Producer for stream {StreamId}."
        )]
        private partial void LogInfoUnregisterProducer(QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_07,
            Message = "Failed to unregister myself as stream producer to some streams that used to be in my responsibility."
        )]
        private partial void LogWarningUnregisterProducer(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_09,
            Message = "AddSubscriber: Stream={StreamId} Subscriber={SubscriberId}."
        )]
        private partial void LogDebugAddSubscriber(QualifiedStreamId streamId, GrainId subscriberId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_10,
            Message = "Removed consumer: subscription {SubscriptionId}, for stream {StreamId}."
        )]
        private partial void LogDebugRemovedConsumer(GuidId subscriptionId, QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_12,
            Message = "Giving up reading from queue {QueueId} after retry attempts {ReadLoopRetryMax}"
        )]
        private partial void LogErrorGivingUpReading(QueueIdLogRecord queueId, int readLoopRetryMax, Exception exception);

        [LoggerMessage(

            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_12,
            Message = "Exception while retrying the {RetryCounter}th time reading from queue {QueueId}"
        )]
        private partial void LogErrorRetrying(int retryCounter, QueueIdLogRecord queueId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_27,
            Message = "Exception calling MessagesDeliveredAsync on queue {MyQueueId}. Ignoring."
        )]
        private partial void LogWarningMessagesDeliveredAsync(QueueIdLogRecord myQueueId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_24,
            Message = "Stream cache is under pressure. Backing off."
        )]
        private partial void LogInfoStreamCacheUnderPressure();

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_11,
            Message = "Got {ReceivedCount} messages from queue {Queue}. So far {MessageCount} messages from this queue."
        )]
        private partial void LogTraceGotMessages(int receivedCount, QueueIdLogRecord queue, int messageCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Pulled new messages in stream {StreamId} from the queue, but the subscriber isn't fully registered yet. The pulling agent will start deliver on this stream after registration is complete."
        )]
        private partial void LogDebugPulledNewMessages(QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_14,
            Message = "Exception while trying to deliver msgs to stream {StreamId} in PersistentStreamPullingAgentGrain.RunConsumerCursor"
        )]
        private partial void LogErrorDeliveringMessages(QualifiedStreamId streamId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_15,
            Message = "Ignored RunConsumerCursor error"
        )]
        private partial void LogErrorRunConsumerCursor(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to deliver message to consumer on {SubscriptionId} for stream {StreamId}, may retry."
        )]
        private partial void LogWarningFailedToDeliverMessage(GuidId subscriptionId, QualifiedStreamId streamId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.Stream_ConsumerIsDead,
            Message = "Consumer {Consumer} on stream {StreamId} is no longer active - permanently removing Consumer."
        )]
        private partial void LogWarningConsumerIsDead(IStreamConsumerExtension consumer, QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_17,
            Message = "RegisterAsStreamProducer failed"
        )]
        private static partial void LogErrorRegisterAsStreamProducer(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_17,
            Message = "Failed to register stream {StreamId}."
        )]
        private partial void LogWarningFailedToRegisterStream(QualifiedStreamId streamId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_16,
            Message = "Got back {Count} subscribers for stream {StreamId}."
        )]
        private partial void LogDebugGotBackSubscribers(int count, QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_26,
            Message = "Failed to add subscription for stream {StreamId}."
        )]
        private partial void LogWarningFailedToAddSubscription(QualifiedStreamId streamId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_13,
            Message = "Ignoring exception while trying to evaluate subscription filter '{Filter}' with data '{FilterData}' on stream {StreamId}"
        )]
        private partial void LogWarningFilterEvaluation(string filter, string filterData, StreamId streamId, Exception exception);
    }
}
