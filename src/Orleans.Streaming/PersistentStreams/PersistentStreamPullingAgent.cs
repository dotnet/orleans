using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Streams.Filtering;

namespace Orleans.Streams
{
    internal class PersistentStreamPullingAgent : SystemTarget, IPersistentStreamPullingAgent
    {
        private static readonly IBackoffProvider DeliveryBackoffProvider = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        private static readonly IBackoffProvider ReadLoopBackoff = new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        private const int ReadLoopRetryMax = 6;
        private const int StreamInactivityCheckFrequency = 10;
        private readonly string streamProviderName;
        private readonly IStreamPubSub pubSub;
        private readonly IStreamFilter streamFilter;
        private readonly Dictionary<InternalStreamId, StreamConsumerCollection> pubSubCache;
        private readonly StreamPullingAgentOptions options;
        private readonly ILogger logger;
        private readonly CounterStatistic numReadMessagesCounter;
        private readonly CounterStatistic numSentMessagesCounter;
        private int numMessages;

        private readonly IQueueAdapter queueAdapter;
        private readonly IStreamFailureHandler streamFailureHandler;
        private IQueueCache queueCache;
        private IQueueAdapterReceiver receiver;
        private DateTime lastTimeCleanedPubSubCache;
        private IDisposable timer;

        internal readonly QueueId QueueId;
        private Task receiverInitTask;
        private bool IsShutdown => timer == null;
        private string StatisticUniquePostfix => streamProviderName + "." + QueueId;

        internal PersistentStreamPullingAgent(
            SystemTargetGrainId id,
            string strProviderName,
            ILoggerFactory loggerFactory,
            IStreamPubSub streamPubSub,
            IStreamFilter streamFilter,
            QueueId queueId,
            StreamPullingAgentOptions options,
            SiloAddress siloAddress,
            IQueueAdapter queueAdapter,
            IQueueAdapterCache queueAdapterCache,
            IStreamFailureHandler streamFailureHandler)
            : base(id, siloAddress, true, loggerFactory)
        {
            if (strProviderName == null) throw new ArgumentNullException("runtime", "PersistentStreamPullingAgent: strProviderName should not be null");

            QueueId = queueId;
            streamProviderName = strProviderName;
            pubSub = streamPubSub;
            this.streamFilter = streamFilter;
            pubSubCache = new Dictionary<InternalStreamId, StreamConsumerCollection>();
            this.options = options;
            this.queueAdapter = queueAdapter ?? throw new ArgumentNullException(nameof(queueAdapter));
            this.streamFailureHandler = streamFailureHandler ?? throw new ArgumentNullException(nameof(streamFailureHandler)); ;
            numMessages = 0;

            logger = loggerFactory.CreateLogger($"{this.GetType().Namespace}.{streamProviderName}");
            logger.Info(ErrorCode.PersistentStreamPullingAgent_01,
                "Created {0} {1} for Stream Provider {2} on silo {3} for Queue {4}.",
                GetType().Name, ((ISystemTargetBase)this).GrainId.ToString(), streamProviderName, Silo, QueueId.ToStringWithHashCode());
            numReadMessagesCounter = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES, StatisticUniquePostfix));
            numSentMessagesCounter = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES, StatisticUniquePostfix));
            // TODO: move queue cache size statistics tracking into queue cache implementation once Telemetry APIs and LogStatistics have been reconciled.
            //IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_QUEUE_CACHE_SIZE, statUniquePostfix), () => queueCache != null ? queueCache.Size : 0);

            try
            {
                receiver = queueAdapter.CreateReceiver(QueueId);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.PersistentStreamPullingAgent_02, "Exception while calling IQueueAdapter.CreateNewReceiver.", exc);
                throw;
            }

            try
            {
                if (queueAdapterCache != null)
                {
                    queueCache = queueAdapterCache.CreateQueueCache(QueueId);
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.PersistentStreamPullingAgent_23, "Exception while calling IQueueAdapterCache.CreateQueueCache.", exc);
                throw;
            }
        }

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
            return OrleansTaskExtentions.WrapInTask(() => InitializeInternal());
        }

        private void InitializeInternal()
        {
            logger.Info(ErrorCode.PersistentStreamPullingAgent_02, "Init of {0} {1} on silo {2} for queue {3}.",
                GetType().Name, ((ISystemTargetBase)this).GrainId.ToString(), Silo, QueueId.ToStringWithHashCode());

            lastTimeCleanedPubSubCache = DateTime.UtcNow;

            try
            {
                receiverInitTask = OrleansTaskExtentions.SafeExecute(() => receiver.Initialize(this.options.InitQueueTimeout))
                    .LogException(logger, ErrorCode.PersistentStreamPullingAgent_03, $"QueueAdapterReceiver {QueueId.ToStringWithHashCode()} failed to Initialize.");
                receiverInitTask.Ignore();
            }
            catch
            {
                // Just ignore this exception and proceed as if Initialize has succeeded.
                // We already logged individual exceptions for individual calls to Initialize. No need to log again.
            }

            // Setup a reader for a new receiver. 
            // Even if the receiver failed to initialise, treat it as OK and start pumping it. It's receiver responsibility to retry initialization.
            var randomTimerOffset = ThreadSafeRandom.NextTimeSpan(this.options.GetQueueMsgsTimerPeriod);
            timer = RegisterTimer(AsyncTimerCallback, QueueId, randomTimerOffset, this.options.GetQueueMsgsTimerPeriod);

            IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE, StatisticUniquePostfix), () => pubSubCache.Count);

            logger.Info((int)ErrorCode.PersistentStreamPullingAgent_04, "Taking queue {0} under my responsibility.", QueueId.ToStringWithHashCode());
        }

        public async Task Shutdown()
        {
            // Stop pulling from queues that are not in my range anymore.
            logger.Info(ErrorCode.PersistentStreamPullingAgent_05, "Shutdown of {0} responsible for queue: {1}", GetType().Name, QueueId.ToStringWithHashCode());

            if (timer != null)
            {
                IDisposable tmp = timer;
                timer = null;
                Utils.SafeExecute(tmp.Dispose, this.logger);
            }

            this.queueCache = null;

            Task localReceiverInitTask = receiverInitTask;
            if (localReceiverInitTask != null)
            {
                try
                {
                    await localReceiverInitTask;
                    receiverInitTask = null;
                }
                catch (Exception)
                {
                    receiverInitTask = null;
                    // squelch
                }
            }

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

            var unregisterTasks = new List<Task>();
            var meAsStreamProducer = this.AsReference<IStreamProducerExtension>();
            foreach (var tuple in pubSubCache)
            {
                tuple.Value.DisposeAll(logger);
                var streamId = tuple.Key;
                logger.Info(ErrorCode.PersistentStreamPullingAgent_06, "Unregister PersistentStreamPullingAgent Producer for stream {0}.", streamId);
                unregisterTasks.Add(pubSub.UnregisterProducer(streamId, meAsStreamProducer));
            }

            try
            {
                await Task.WhenAll(unregisterTasks);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.PersistentStreamPullingAgent_08,
                    "Failed to unregister myself as stream producer to some streams that used to be in my responsibility.", exc);
            }
            pubSubCache.Clear();
            IntValueStatistic.Delete(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE, StatisticUniquePostfix));
            //IntValueStatistic.Delete(new StatisticName(StatisticNames.STREAMS_PERSISTENT_STREAM_QUEUE_CACHE_SIZE, StatisticUniquePostfix));
        }

        public Task AddSubscriber(
            GuidId subscriptionId,
            InternalStreamId streamId,
            IStreamConsumerExtension streamConsumer,
            string filterData)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.PersistentStreamPullingAgent_09, "AddSubscriber: Stream={0} Subscriber={1}.", streamId, streamConsumer);
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
            InternalStreamId streamId,
            IStreamConsumerExtension streamConsumer,
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
                data = streamDataCollection.AddConsumer(subscriptionId, streamId, streamConsumer, filterData);

            if (await DoHandshakeWithConsumer(data, cacheToken))
            {
                if (data.State == StreamConsumerDataState.Inactive)
                    RunConsumerCursor(data).Ignore(); // Start delivering events if not actively doing so
            }
        }

        private async Task<bool> DoHandshakeWithConsumer(
            StreamConsumerData consumerData,
            StreamSequenceToken cacheToken)
        {
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
                         (exception, i) => exception is not ClientNotAvailableException && !IsShutdown,
                         this.options.MaxEventDeliveryTime,
                         DeliveryBackoffProvider);

                    if (requestedHandshakeToken != null)
                    {
                        consumerData.SafeDisposeCursor(logger);
                        consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, requestedHandshakeToken.Token);
                    }
                    else
                    {
                        if (consumerData.Cursor == null) // if the consumer did not ask for a specific token and we already have a cursor, jsut keep using it.
                            consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, cacheToken);
                    }
                }
                catch (Exception exception)
                {
                    exceptionOccured = exception;
                }
                if (exceptionOccured != null)
                {
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
                    consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, cacheToken);
                }
                catch (Exception)
                {
                    consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, null); // just in case last GetCacheCursor failed.
                }
            }
            return true;
        }

        public Task RemoveSubscriber(GuidId subscriptionId, InternalStreamId streamId)
        {
            RemoveSubscriber_Impl(subscriptionId, streamId);
            return Task.CompletedTask;
        }

        public void RemoveSubscriber_Impl(GuidId subscriptionId, InternalStreamId streamId)
        {
            if (IsShutdown) return;

            StreamConsumerCollection streamData;
            if (!pubSubCache.TryGetValue(streamId, out streamData)) return;

            // remove consumer
            bool removed = streamData.RemoveConsumer(subscriptionId, logger);
            if (removed && logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.PersistentStreamPullingAgent_10, "Removed Consumer: subscription={0}, for stream {1}.", subscriptionId, streamId);

            if (streamData.Count == 0)
                pubSubCache.Remove(streamId);
        }

        private async Task AsyncTimerCallback(object state)
        {
            var queueId = (QueueId)state;
            try
            {
                Task localReceiverInitTask = receiverInitTask;
                if (localReceiverInitTask != null)
                {
                    await localReceiverInitTask;
                    receiverInitTask = null;
                }

                if (IsShutdown) return; // timer was already removed, last tick

                // loop through the queue until it is empty.
                while (!IsShutdown) // timer will be set to null when we are asked to shudown. 
                {
                    int maxCacheAddCount = queueCache?.GetMaxAddCount() ?? QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG;
                    if (maxCacheAddCount != QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG && maxCacheAddCount <= 0)
                        return;

                    // If read succeeds and there is more data, we continue reading.
                    // If read succeeds and there is no more data, we break out of loop
                    // If read fails, we retry 6 more times, with backoff policy.
                    //    we log each failure as warnings. After 6 times retry if still fail, we break out of loop and log an error
                    bool moreData = await AsyncExecutorWithRetries.ExecuteWithRetries(
                        i => ReadFromQueue(queueId, receiver, maxCacheAddCount),
                        ReadLoopRetryMax,
                        ReadLoopRetryExceptionFilter,
                        Constants.INFINITE_TIMESPAN,
                        ReadLoopBackoff);
                    if (!moreData)
                        return;
                }
            }
            catch (Exception exc)
            {
                receiverInitTask = null;
                logger.Error(ErrorCode.PersistentStreamPullingAgent_12, $"Giving up reading from queue {queueId} after retry attempts {ReadLoopRetryMax}", exc);
            }
        }

        private bool ReadLoopRetryExceptionFilter(Exception e, int retryCounter)
        {
            this.logger.Warn(ErrorCode.PersistentStreamPullingAgent_12, $"Exception while retrying the {retryCounter}th time reading from queue {this.QueueId}", e);
            return !IsShutdown;
        }

        /// <summary>
        /// Read from queue.
        /// Returns true, if data was read, false if it was not
        /// </summary>
        /// <param name="myQueueId"></param>
        /// <param name="rcvr"></param>
        /// <param name="maxCacheAddCount"></param>
        /// <returns></returns>
        private async Task<bool> ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver rcvr, int maxCacheAddCount)
        {
            if (rcvr == null)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            // Try to cleanup the pubsub cache at the cadence of 10 times in the configurable StreamInactivityPeriod.
            if ((now - lastTimeCleanedPubSubCache) >= this.options.StreamInactivityPeriod.Divide(StreamInactivityCheckFrequency))
            {
                lastTimeCleanedPubSubCache = now;
                CleanupPubSubCache(now);
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
                        logger.Warn(ErrorCode.PersistentStreamPullingAgent_27,
                            $"Exception calling MessagesDeliveredAsync on queue {myQueueId}. Ignoring.", exc);
                    }
                }
            }

            if (queueCache != null && queueCache.IsUnderPressure())
            {
                // Under back pressure. Exit the loop. Will attempt again in the next timer callback.
                logger.Info((int)ErrorCode.PersistentStreamPullingAgent_24, "Stream cache is under pressure. Backing off.");
                return false;
            }

            // Retrieve one multiBatch from the queue. Every multiBatch has an IEnumerable of IBatchContainers, each IBatchContainer may have multiple events.
            IList<IBatchContainer> multiBatch = await rcvr.GetQueueMessagesAsync(maxCacheAddCount);

            if (multiBatch == null || multiBatch.Count == 0) return false; // queue is empty. Exit the loop. Will attempt again in the next timer callback.

            queueCache?.AddToCache(multiBatch);
            numMessages += multiBatch.Count;
            numReadMessagesCounter.IncrementBy(multiBatch.Count);
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.PersistentStreamPullingAgent_11, "Got {0} messages from queue {1}. So far {2} msgs from this queue.",
                multiBatch.Count, myQueueId.ToStringWithHashCode(), numMessages);

            foreach (var group in
                multiBatch
                .Where(m => m != null)
                .GroupBy(container => container.StreamId))
            {
                var streamId = new InternalStreamId(queueAdapter.Name, group.Key);
                StreamSequenceToken startToken = group.First().SequenceToken;
                StreamConsumerCollection streamData;
                if (pubSubCache.TryGetValue(streamId, out streamData))
                {
                    streamData.RefreshActivity(now);
                    if (streamData.StreamRegistered)
                    {
                        StartInactiveCursors(streamData,
                            startToken); // if this is an existing stream, start any inactive cursors
                    }
                    else
                    {
                        if(this.logger.IsEnabled(LogLevel.Debug))
                            this.logger.LogDebug($"Pulled new messages in stream {streamId} from the queue, but pulling agent haven't succeeded in" +
                                                   $"RegisterStream yet, will start deliver on this stream after RegisterStream succeeded");
                    }

                }
                else
                {
                    RegisterStream(streamId, startToken, now).Ignore(); // if this is a new stream register as producer of stream in pub sub system
                }
            }
            return true;
        }

        private void CleanupPubSubCache(DateTime now)
        {
            if (pubSubCache.Count == 0) return;
            var toRemove = pubSubCache.Where(pair => pair.Value.IsInactive(now, this.options.StreamInactivityPeriod))
                         .ToList();
            toRemove.ForEach(tuple =>
            {
                pubSubCache.Remove(tuple.Key);
                tuple.Value.DisposeAll(logger);
            });
        }

        private async Task RegisterStream(InternalStreamId streamId, StreamSequenceToken firstToken, DateTime now)
        {
            var streamData = new StreamConsumerCollection(now);
            pubSubCache.Add(streamId, streamData);
            // Create a fake cursor to point into a cache.
            // That way we will not purge the event from the cache, until we talk to pub sub.
            // This will help ensure the "casual consistency" between pre-existing subscripton (of a potentially new already subscribed consumer) 
            // and later production.
            var pinCursor = queueCache?.GetCacheCursor(streamId, firstToken);

            try
            {
                await RegisterAsStreamProducer(streamId, firstToken);
                streamData.StreamRegistered = true;
            }
            finally
            {
                // Cleanup the fake pinning cursor.
                pinCursor?.Dispose();
            }
        }

        private void StartInactiveCursors(StreamConsumerCollection streamData, StreamSequenceToken startToken)
        {
            foreach (StreamConsumerData consumerData in streamData.AllConsumers())
            {
                consumerData.Cursor?.Refresh(startToken);
                if (consumerData.State == StreamConsumerDataState.Inactive)
                {
                    // wake up inactive consumers
                    RunConsumerCursor(consumerData).Ignore();
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
                while (consumerData.Cursor != null)
                {
                    IBatchContainer batch = null;
                    Exception exceptionOccured = null;
                    try
                    {
                        batch = GetBatchForConsumer(consumerData.Cursor, consumerData.StreamId, consumerData.FilterData);
                        if (batch == null)
                        {
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
                        if (!ShouldDeliverBatch(consumerData.StreamId, batch, consumerData.FilterData))
                            continue;
                    }

                    try
                    {
                        numSentMessagesCounter.Increment();
                        if (batch != null)
                        {
                            StreamHandshakeToken newToken = await AsyncExecutorWithRetries.ExecuteWithRetries(
                                i => DeliverBatchToConsumer(consumerData, batch),
                                AsyncExecutorWithRetries.INFINITE_RETRIES,
                                // Do not retry if the agent is shutting down, or if the exception is ClientNotAvailableException
                                (exception, i) => exception is not ClientNotAvailableException && !IsShutdown, 
                                this.options.MaxEventDeliveryTime,
                                DeliveryBackoffProvider);
                            if (newToken != null)
                            {
                                consumerData.LastToken = newToken;
                                IQueueCacheCursor newCursor = queueCache.GetCacheCursor(consumerData.StreamId, newToken.Token);
                                consumerData.SafeDisposeCursor(logger);
                                consumerData.Cursor = newCursor;
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        consumerData.Cursor?.RecordDeliveryFailure();
                        var message =
                            $"Exception while trying to deliver msgs to stream {consumerData.StreamId} in PersistentStreamPullingAgentGrain.RunConsumerCursor";
                        logger.Error(ErrorCode.PersistentStreamPullingAgent_14, message, exc);
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
                logger.Error(ErrorCode.PersistentStreamPullingAgent_15, "Ignored RunConsumerCursor Error", exc);
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
                return newToken;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to deliver message to consumer on {SubscriptionId} for stream {StreamId}, may retry.", consumerData.SubscriptionId, consumerData.StreamId);
                throw;
            }
        }

        /// <summary>
        /// Add call context for batch delivery call, then clear context immediately, without giving up turn.
        /// </summary>
        private Task<StreamHandshakeToken> ContextualizedDeliverBatchToConsumer(StreamConsumerData consumerData, IBatchContainer batch)
        {
            bool isRequestContextSet = batch.ImportRequestContext();
            try
            {
                return consumerData.StreamConsumer.DeliverBatch(consumerData.SubscriptionId, consumerData.StreamId, batch.AsImmutable(), consumerData.LastToken);
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
                logger.Warn(ErrorCode.Stream_ConsumerIsDead,
                    "Consumer {0} on stream {1} is no longer active - permanently removing Consumer.", consumerData.StreamConsumer, consumerData.StreamId);
                pubSub.UnregisterConsumer(consumerData.SubscriptionId, consumerData.StreamId).Ignore();
                return true;
            }

            // notify consumer about the error or that the data is not available.
            await OrleansTaskExtentions.ExecuteAndIgnoreException(
                () => DeliverErrorToConsumer(
                    consumerData, exceptionOccured, batch));
            // record that there was a delivery failure
            if (isDeliveryError)
            {
                await OrleansTaskExtentions.ExecuteAndIgnoreException(
                    () => streamFailureHandler.OnDeliveryFailure(
                        consumerData.SubscriptionId, streamProviderName, consumerData.StreamId, token));
            }
            else
            {
                await OrleansTaskExtentions.ExecuteAndIgnoreException(
                       () => streamFailureHandler.OnSubscriptionFailure(
                           consumerData.SubscriptionId, streamProviderName, consumerData.StreamId, token));
            }
            // if configured to fault on delivery failure and this is not an implicit subscription, fault and remove the subscription
            if (streamFailureHandler.ShouldFaultSubsriptionOnError && !SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
            {
                try
                {
                    // notify consumer of faulted subscription, if we can.
                    await OrleansTaskExtentions.ExecuteAndIgnoreException(
                        () => DeliverErrorToConsumer(
                            consumerData, new FaultedSubscriptionException(consumerData.SubscriptionId, consumerData.StreamId), batch));
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

        private static async Task<ISet<PubSubSubscriptionState>> PubsubRegisterProducer(IStreamPubSub pubSub, InternalStreamId streamId,
            IStreamProducerExtension meAsStreamProducer, ILogger logger)
        {
            try
            {
                var streamData = await pubSub.RegisterProducer(streamId, meAsStreamProducer);
                return streamData;
            }
            catch (Exception e)
            {
                logger.Error(ErrorCode.PersistentStreamPullingAgent_17, $"RegisterAsStreamProducer failed due to {e}", e);
                throw;
            }
        }

        private async Task RegisterAsStreamProducer(InternalStreamId streamId, StreamSequenceToken streamStartToken)
        {
            try
            {
                if (pubSub == null) throw new NullReferenceException("Found pubSub reference not set up correctly in RetreaveNewStream");

                IStreamProducerExtension meAsStreamProducer = this.AsReference<IStreamProducerExtension>();
                ISet<PubSubSubscriptionState> streamData = null;
                await AsyncExecutorWithRetries.ExecuteWithRetries(
                                async i => { streamData = 
                                    await PubsubRegisterProducer(pubSub, streamId, meAsStreamProducer, logger); },
                                AsyncExecutorWithRetries.INFINITE_RETRIES,
                                (exception, i) => !IsShutdown,
                                Constants.INFINITE_TIMESPAN,
                                DeliveryBackoffProvider);
               
                
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.PersistentStreamPullingAgent_16, "Got back {0} Subscribers for stream {1}.", streamData.Count, streamId);

                var addSubscriptionTasks = new List<Task>(streamData.Count);
                foreach (PubSubSubscriptionState item in streamData)
                {
                    addSubscriptionTasks.Add(AddSubscriber_Impl(item.SubscriptionId, item.Stream, item.Consumer, item.FilterData, streamStartToken));
                }
                await Task.WhenAll(addSubscriptionTasks);
            }
            catch (Exception exc)
            {
                // RegisterAsStreamProducer is fired with .Ignore so we should log if anything goes wrong, because there is no one to catch the exception
                logger.Error(ErrorCode.PersistentStreamPullingAgent_17, "Ignored RegisterAsStreamProducer Error", exc);
                throw;
            }
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
                var message = $"Ignoring exception while trying to evaluate subscription filter '{this.streamFilter.GetType().Name}' with data '{filterData}' on stream {streamId}";
                logger.Warn((int)ErrorCode.PersistentStreamPullingAgent_13, message, exc);
            }
            return true;
        }
    }
}
