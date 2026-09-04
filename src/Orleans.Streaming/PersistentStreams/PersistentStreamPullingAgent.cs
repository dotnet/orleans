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
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Streaming;
using Orleans.Streams.Filtering;
using StreamingEvents = Orleans.Streaming.Diagnostics.StreamingEvents;
using TagList = System.Diagnostics.TagList;

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
        private readonly StreamInstruments? _streamInstruments;
        private readonly TimeProvider _timeProvider;
        internal readonly QueueId QueueId;

        private int numMessages;
        private IQueueCache? queueCache;
        private IQueueAdapterReceiver? receiver;
        private DateTime lastTimeCleanedPubSubCache;
        private IGrainTimer? timer;
        private ITimer? deliveryProgressTimer;

        private Task? receiverInitTask;
        private Task _activePumpTask = Task.CompletedTask;
        private AdmissionGate _workAdmission = new();
        private Task? _shutdownTask;
        private StreamSequenceToken? _lastReadToken;
        private bool _useLegacyDeliveryProgress;
        private bool IsShutdown => timer is null;
        private string StatisticUniquePostfix => $"{streamProviderName}.{QueueId}";

        internal interface ITestAccessor
        {
            Task<bool> ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver? receiver, int maxCacheAddCount);
            Task<bool> ReadFromQueueWithCancellation(QueueId myQueueId, IQueueAdapterReceiver? receiver, int maxCacheAddCount, CancellationToken cancellationToken);
            Task RegisterStream(QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now);
            IQueueCacheCursor GetRecoveryCursor(StreamConsumerData consumerData);
            Task<IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>> GetPubSubCache();
            Task AddSubscriber(StreamConsumerData consumerData);
            Task<bool> DoHandshakeWithConsumer(StreamConsumerData consumerData, StreamSequenceToken? cacheToken);
            Task RunConsumerCursor(StreamConsumerData consumerData);
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
            SystemTargetShared shared,
            StreamInstruments? streamInstruments = null)
            : base(id, shared)
        {
            if (strProviderName == null) throw new ArgumentNullException(nameof(strProviderName), "PersistentStreamPullingAgent: strProviderName should not be null");

            QueueId = queueId;
            streamProviderName = strProviderName;
            pubSub = streamPubSub;
            this.streamFilter = streamFilter;
            pubSubCache = new Dictionary<QualifiedStreamId, StreamConsumerCollection>();
            this.options = options;
            options.InitialSubscriptionStartPosition.Validate();
            if (options.DeliveryProgressUpdateInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.DeliveryProgressUpdateInterval,
                    "The delivery progress update interval must be greater than zero.");
            }

            this.queueAdapter = queueAdapter ?? throw new ArgumentNullException(nameof(queueAdapter));
            this.streamFailureHandler = streamFailureHandler ?? throw new ArgumentNullException(nameof(streamFailureHandler));
            this.queueAdapterCache = queueAdapterCache;
            this.deliveryBackoffProvider = deliveryBackoffProvider;
            this.queueReaderBackoffProvider = queueReaderBackoffProvider;
            _streamInstruments = streamInstruments;
            _timeProvider = timeProvider ?? TimeProvider.System;
            numMessages = 0;

            logger = shared.LoggerFactory.CreateLogger($"{this.GetType().Namespace}.{streamProviderName}");
            LogInfoCreated(GetType().Name, GrainId, strProviderName, Silo, new(QueueId));
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        Task<bool> ITestAccessor.ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver? receiver, int maxCacheAddCount)
            => this.RunOrQueueTaskResult(() => ReadFromQueue(myQueueId, receiver, maxCacheAddCount, CancellationToken.None)).Unwrap();

        Task<bool> ITestAccessor.ReadFromQueueWithCancellation(
            QueueId myQueueId,
            IQueueAdapterReceiver? receiver,
            int maxCacheAddCount,
            CancellationToken cancellationToken)
            => this.RunOrQueueTaskResult(() => ReadFromQueue(myQueueId, receiver, maxCacheAddCount, cancellationToken)).Unwrap();

        Task ITestAccessor.RegisterStream(QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now)
            => this.RunOrQueueTaskResult(() =>
            {
                RegisterStream(streamId, firstToken, now, CancellationToken.None);

                if (pubSubCache.TryGetValue(streamId, out var streamData) && streamData.RegistrationTask is { } registrationTask)
                {
                    return registrationTask;
                }

                return Task.CompletedTask;
            }).Unwrap();

        IQueueCacheCursor ITestAccessor.GetRecoveryCursor(StreamConsumerData consumerData)
            => GetRecoveryCursor(consumerData);

        Task<IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>> ITestAccessor.GetPubSubCache()
            => this.RunOrQueueTaskResult(() => (IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>)new Dictionary<QualifiedStreamId, StreamConsumerCollection>(pubSubCache));

        Task ITestAccessor.AddSubscriber(StreamConsumerData consumerData)
            => this.RunOrQueueTask(() => AddSubscriber_Impl(
                consumerData.SubscriptionId, consumerData.StreamId, default, consumerData.FilterData, null));

        Task<bool> ITestAccessor.DoHandshakeWithConsumer(StreamConsumerData consumerData, StreamSequenceToken? cacheToken)
            => this.RunOrQueueTaskResult(() => DoHandshakeWithConsumer(consumerData, cacheToken, ++consumerData.HandshakeRequestId)).Unwrap();

        Task ITestAccessor.RunConsumerCursor(StreamConsumerData consumerData)
            => this.RunOrQueueTask(() => RunConsumerCursor(consumerData));

        Task ITestAccessor.RunQueuePump(QueueId myQueueId, CancellationToken cancellationToken)
            => this.RunOrQueueTask(() => RunQueuePump(myQueueId, cancellationToken));

        Task ITestAccessor.Shutdown() => this.RunOrQueueTask(() => Shutdown(CancellationToken.None));

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
        public async Task Initialize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_shutdownTask is { } shutdownTask)
            {
                await shutdownTask.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _shutdownTask = null;
                _workAdmission = new();
            }

            LogInfoInit(GetType().Name, GrainId, Silo, new(QueueId));

            _activePumpTask = Task.CompletedTask;
            _lastReadToken = null;
            _useLegacyDeliveryProgress = false;
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
                receiverInitTask = OrleansTaskExtentions.SafeExecute(InitializeReceiver)
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
            deliveryProgressTimer = _timeProvider.CreateTimer(
                static state => ((PersistentStreamPullingAgent)state!).ScheduleDeliveryProgressUpdate(),
                this,
                this.options.DeliveryProgressUpdateInterval,
                this.options.DeliveryProgressUpdateInterval);
            StreamingEvents.EmitPullingAgentStarted(streamProviderName, Silo, QueueId, randomTimerOffset, this.options.GetQueueMsgsTimerPeriod);

            _streamInstruments?.RegisterPersistentStreamPubSubCacheSizeObserve(() => new Measurement<int>(pubSubCache.Count, new KeyValuePair<string, object?>("name", StatisticUniquePostfix)));

            LogInfoTakingQueue(new(QueueId));

            async Task InitializeReceiver()
            {
                try
                {
                    await receiver.Initialize(this.options.InitQueueTimeout, cancellationToken);
                    StreamingEvents.EmitQueueReceiverInitialized(streamProviderName, Silo, QueueId);
                }
                catch (Exception exception)
                {
                    StreamingEvents.EmitQueueReceiverInitializationFailed(streamProviderName, Silo, QueueId, exception);
                    throw;
                }
            }
        }

        public Task Shutdown(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _shutdownTask ??= ShutdownCore(cancellationToken);
        }

        private async Task ShutdownCore(CancellationToken cancellationToken)
        {
            // Stop pulling from queues that are not in my range anymore.
            LogInfoShutdown(GetType().Name, new(QueueId));

            var drainTask = _workAdmission.CloseAsync();
            var asyncTimer = timer;
            timer = null;
            var localDeliveryProgressTimer = deliveryProgressTimer;
            deliveryProgressTimer = null;
            localDeliveryProgressTimer?.Dispose();
            if (asyncTimer is not null)
            {
                asyncTimer.Dispose();
                StreamingEvents.EmitPullingAgentStopped(streamProviderName, Silo, QueueId);
            }

            // Pending registrations leave subscriber progress uncertain, even if they exit during the drain.
            var hasPendingRegistrations = pubSubCache.Values.Any(static stream => stream.RegistrationTask is { IsCompleted: false });

            Task? localReceiverInitTask = receiverInitTask;
            if (localReceiverInitTask != null)
            {
                await localReceiverInitTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                receiverInitTask = null;
            }

            await _activePumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await drainTask;

            // All accepted work has finished progress bookkeeping and released its batch/registration pins.
            if (!hasPendingRegistrations)
            {
                NotifyDeliveryProgress();
            }

            foreach (var streamData in pubSubCache.Values)
            {
                streamData.DisposeAll(logger);
            }

            this.queueCache = null;

            try
            {
                IQueueAdapterReceiver? localReceiver = this.receiver;
                this.receiver = null;
                if (localReceiver != null)
                {
                    var task = OrleansTaskExtentions.SafeExecute(() => localReceiver.Shutdown(this.options.InitQueueTimeout, cancellationToken));
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
            foreach (var tuple in pubSubCache)
            {
                var streamId = tuple.Key;
                LogInfoUnregisterProducer(streamId);
                unregisterTasks.Add(pubSub.UnregisterProducer(streamId, GrainId, cancellationToken));
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
            string? filterData,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            string? filterData,
            StreamSequenceToken? cacheToken)
        {
            using var admission = _workAdmission.TryEnter();
            if (!admission.Entered || IsShutdown) return;

            if (!pubSubCache.TryGetValue(streamId, out var streamDataCollection))
            {
                // If stream is not in pubsub cache, then we've received no events on this stream, and will aquire the subscriptions from pubsub when we do.
                return;
            }

            if (!streamDataCollection.TryGetConsumer(subscriptionId, out var data))
            {
                var consumerReference = this.RuntimeClient.InternalGrainFactory
                    .GetGrain(streamConsumer)
                    .AsReference<IStreamConsumerExtension>();
                data = streamDataCollection.AddConsumer(subscriptionId, streamId, consumerReference, filterData, _timeProvider.GetUtcNow().UtcDateTime);
                StreamingEvents.EmitSubscriptionAdded(streamProviderName, streamId.StreamId, subscriptionId.Guid, streamConsumer, Silo);
            }

            data.IsCaughtUp = false;
            if (data.PendingHandshakes != 0 || data.State == StreamConsumerDataState.Active)
            {
                _useLegacyDeliveryProgress = true;
            }

            data.PendingHandshakes++;
            var handshakeRequestId = ++data.HandshakeRequestId;
            data.HasUnresolvedHandshake = true;
            var handshakeSucceeded = false;
            try
            {
                handshakeSucceeded = await DoHandshakeWithConsumer(data, cacheToken, handshakeRequestId);
                if (handshakeSucceeded && handshakeRequestId == data.HandshakeRequestId)
                {
                    // Delivery can start while the handshake is awaiting a response.
                    if (data.State == StreamConsumerDataState.Active)
                    {
                        _useLegacyDeliveryProgress = true;
                    }

                    data.LastProcessedToken = GetInitialDeliveryProgress(data.LastToken, data.LastProcessedToken);
                    data.PendingStartToken = null;
                    data.IsRegistered = true;
                    StreamingEvents.EmitSubscriptionAttached(streamProviderName, streamId.StreamId, subscriptionId.Guid, streamConsumer, Silo);
                    if (data.State == StreamConsumerDataState.Inactive)
                        RunConsumerCursor(data).Ignore(); // Start delivering events if not actively doing so
                }
            }
            finally
            {
                if (handshakeRequestId == data.HandshakeRequestId)
                {
                    data.HasUnresolvedHandshake = !handshakeSucceeded;
                }
                data.PendingHandshakes--;
            }
        }

        internal static StreamSequenceToken? GetInitialDeliveryProgress(
            StreamHandshakeToken? handshakeToken,
            StreamSequenceToken? currentProgress = null)
            => handshakeToken switch
            {
                DeliveryToken deliveryToken => deliveryToken.Token,
                StartToken => null,
                _ => currentProgress,
            };

        private async Task<bool> DoHandshakeWithConsumer(
            StreamConsumerData consumerData,
            StreamSequenceToken? cacheToken,
            long handshakeRequestId)
        {
            if (IsShutdown) return false;

            StreamHandshakeToken? requestedHandshakeToken = null;
            StreamHandshakeToken? effectiveHandshakeToken = null;
            var providerDefaultRequest = false;
            var cursorStartToken = cacheToken ?? consumerData.PendingStartToken;
            var cursorRepositioned = false;
            // if not cache, then we can't get cursor and there is no reason to ask consumer for token.
            if (queueCache != null)
            {
                Exception? exceptionOccured = null;
                var forceFaultSubscription = false;
                try
                {
                    requestedHandshakeToken = await AsyncExecutorWithRetries.ExecuteWithRetries(
                         i => consumerData.StreamConsumer.GetSequenceToken(consumerData.SubscriptionId),
                         AsyncExecutorWithRetries.INFINITE_RETRIES,
                         // Do not retry if the agent is shutting down, or if the exception is ClientNotAvailableException
                         (exception, i) => exception is not ClientNotAvailableException && !IsShutdown
                             && handshakeRequestId == consumerData.HandshakeRequestId,
                         this.options.MaxEventDeliveryTime,
                         deliveryBackoffProvider,
                         cancellationToken: CancellationToken.None);

                    if (handshakeRequestId != consumerData.HandshakeRequestId) return false;

                    effectiveHandshakeToken = requestedHandshakeToken;
                    if (effectiveHandshakeToken is null && ShouldApplyInitialSubscriptionStartPosition(consumerData))
                    {
                        effectiveHandshakeToken = consumerData.LastToken as StartPositionToken
                            ?? StreamHandshakeToken.CreateStartPositionToken(options.InitialSubscriptionStartPosition);
                        providerDefaultRequest = true;
                    }

                    if (effectiveHandshakeToken is StartPositionToken startPositionToken)
                    {
                        var preservesCurrentPosition = consumerData.IsRegistered
                            && consumerData.Cursor is not null
                            && startPositionToken.Equals(consumerData.LastToken);
                        if (!preservesCurrentPosition)
                        {
                            consumerData.SafeDisposeCursor(logger);
                            consumerData.Cursor = GetCacheCursorAtStartPosition(
                                consumerData.StreamId,
                                startPositionToken,
                                cacheToken ?? consumerData.PendingStartToken);
                            cursorStartToken = cacheToken ?? consumerData.PendingStartToken;
                            cursorRepositioned = true;
                        }
                    }
                    else if (effectiveHandshakeToken is StartToken or DeliveryToken
                        && effectiveHandshakeToken.Token is { } requestedToken)
                    {
                        var isDeliveryToken = requestedHandshakeToken is DeliveryToken;
                        cursorStartToken = isDeliveryToken
                            ? queueAdapter.IsRewindable
                                ? requestedToken
                                : cacheToken ?? consumerData.PendingStartToken ?? requestedToken
                            : requestedToken;
                        consumerData.SafeDisposeCursor(logger);
                        try
                        {
                            var newCursor = GetCacheCursor(consumerData.StreamId, cursorStartToken);
                            if (isDeliveryToken)
                            {
                                if (newCursor is IQueueCacheCursorProgress progressCursor)
                                {
                                    progressCursor.SetDeliveredThrough(requestedToken);
                                }
                                else
                                {
                                    if (!Equals(cursorStartToken, requestedToken))
                                    {
                                        newCursor.Dispose();
                                        cursorStartToken = requestedToken;
                                        newCursor = GetCacheCursor(consumerData.StreamId, requestedToken);
                                    }
                                    consumerData.PendingBatch = AdvanceCursorPastToken(
                                        newCursor,
                                        requestedToken);
                                }
                            }
                            else if (effectiveHandshakeToken is StartToken
                                && SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
                            {
                                if (newCursor is IQueueCacheCursorProgress progressCursor)
                                {
                                    progressCursor.SetDeliveredThrough(requestedToken);
                                }
                                else
                                {
                                    consumerData.PendingBatch = AdvanceCursorPastToken(
                                        newCursor,
                                        requestedToken);
                                }
                            }

                            consumerData.Cursor = newCursor;
                            consumerData.IsReplayUnavailable = false;
                            cursorRepositioned = true;
                        }
                        catch (QueueCacheMissException) when (cacheToken is not null && !queueAdapter.IsRewindable)
                        {
                            // A cold stream's triggering batch is the receiver's first available
                            // message, so resume there if the consumer's prior token was evicted.
                            consumerData.SafeDisposeCursor(logger);
                            cursorStartToken = cacheToken;
                            consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, cacheToken);
                            if (requestedHandshakeToken is DeliveryToken
                                && consumerData.Cursor is IQueueCacheCursorProgress progressCursor)
                            {
                                progressCursor.SetDeliveredThrough(requestedToken);
                            }

                            cursorRepositioned = true;
                        }
                    }
                    else if (effectiveHandshakeToken is not null)
                    {
                        forceFaultSubscription = true;
                        throw new InvalidOperationException($"Unsupported stream handshake token type {effectiveHandshakeToken.GetType().FullName}.");
                    }
                    else
                    {
                        var registrationToken = cacheToken ?? consumerData.PendingStartToken;
                        if (consumerData.Cursor == null) // if the consumer did not ask for a specific token and we already have a cursor, just keep using it.
                        {
                            consumerData.Cursor = GetCacheCursor(consumerData.StreamId, registrationToken);
                            consumerData.IsReplayUnavailable = false;
                            cursorRepositioned = true;
                        }
                    }
                }
                catch (Exception exception)
                {
                    if (handshakeRequestId != consumerData.HandshakeRequestId) return false;

                    _useLegacyDeliveryProgress = true;
                    exceptionOccured = exception;
                }
                if (exceptionOccured != null)
                {
                    // If we are shutting down, ignore the error
                    if (IsShutdown) return false;

                    bool faultedSubscription = await ErrorProtocol(
                        consumerData,
                        exceptionOccured,
                        false,
                        null,
                        effectiveHandshakeToken?.Token,
                        handshakeRequestId,
                        forceFaultSubscription
                            || effectiveHandshakeToken is StartPositionToken
                                && exceptionOccured is NotSupportedException);
                    if (handshakeRequestId != consumerData.HandshakeRequestId) return false;
                    if (exceptionOccured is DataNotAvailableException)
                    {
                        consumerData.IsReplayUnavailable = true;
                        return false;
                    }

                    var providerFallbackAllowed = providerDefaultRequest
                        && SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid);
                    if (faultedSubscription
                        || effectiveHandshakeToken is StartPositionToken && !providerFallbackAllowed)
                    {
                        return false;
                    }

                    effectiveHandshakeToken = null;
                }
            }
            consumerData.StartPositionIsProviderDefault = providerDefaultRequest
                && effectiveHandshakeToken is StartPositionToken;
            consumerData.LastToken = effectiveHandshakeToken;
            // if we don't yet have a cursor (had errors in the handshake or data not available exc), get a cursor at the event that triggered that consumer subscription.
            if (consumerData.Cursor == null && queueCache != null)
            {
                try
                {
                    var registrationToken = cacheToken ?? consumerData.PendingStartToken;
                    cursorStartToken = registrationToken;
                    consumerData.Cursor = GetCacheCursor(consumerData.StreamId, registrationToken);
                    consumerData.IsReplayUnavailable = false;
                    cursorRepositioned = true;
                }
                catch (Exception) when (!queueAdapter.IsRewindable || cursorStartToken is null)
                {
                    _useLegacyDeliveryProgress = true;
                    consumerData.Cursor = queueCache.GetCacheCursor(consumerData.StreamId, null); // just in case last GetCacheCursor failed.
                    cursorStartToken = null;
                    cursorRepositioned = true;
                }
            }

            if (cursorRepositioned)
            {
                consumerData.CursorStartToken = cursorStartToken;
                if (requestedHandshakeToken is DeliveryToken deliveryToken)
                {
                    consumerData.LastProcessedToken = deliveryToken.Token;
                    consumerData.LastSafePartitionToken = null;
                }
                else
                {
                    // Start/cache/pending tokens are inclusive positions. They become safe only
                    // after the matching record is delivered or intentionally filtered.
                    consumerData.LastProcessedToken = null;
                    consumerData.LastSafePartitionToken = null;
                }
            }
            consumerData.HandshakeGeneration++;
            return true;
        }

        private bool ShouldApplyInitialSubscriptionStartPosition(StreamConsumerData consumerData)
            => options.InitialSubscriptionStartPosition != StreamSubscriptionStartPosition.Latest
                && consumerData.LastProcessedToken is null
                && (!consumerData.IsRegistered
                    || consumerData.LastToken is StartPositionToken startPositionToken
                        && startPositionToken.StartPosition == options.InitialSubscriptionStartPosition);

        private static IBatchContainer? AdvanceCursorPastToken(IQueueCacheCursor cursor, StreamSequenceToken token)
        {
            while (cursor.MoveNext())
            {
                var batch = cursor.GetCurrent(out var exception);
                if (exception is not null)
                {
                    throw exception;
                }

                if (batch is null)
                {
                    throw new InvalidOperationException("A stream cursor returned no current batch after advancing.");
                }

                var comparison = EventSequenceTokenCompatibility.Compare(batch.SequenceToken, token);
                if (comparison >= 0)
                {
                    return comparison > 0 ? batch : null;
                }
            }

            return null;
        }

        private IQueueCacheCursor GetCacheCursorAtStartPosition(
            QualifiedStreamId streamId,
            StartPositionToken startPositionToken,
            StreamSequenceToken? latestBoundary)
            => startPositionToken.StartPosition == StreamSubscriptionStartPosition.Latest
                && latestBoundary is not null
                    ? queueCache!.GetCacheCursor(streamId, latestBoundary)
                    : queueCache!.GetCacheCursorAtPosition(streamId, startPositionToken.StartPosition);

        private IQueueCacheCursor GetRecoveryCursor(StreamConsumerData consumerData)
        {
            if (consumerData.LastProcessedToken is { } lastProcessedToken)
            {
                try
                {
                    return GetCursorAfterProcessedToken(consumerData, lastProcessedToken);
                }
                catch (QueueCacheMissException) when (!queueAdapter.IsRewindable)
                {
                    return queueCache!.GetCacheCursor(consumerData.StreamId, null);
                }
            }

            if (consumerData.LastToken is StartPositionToken startPositionToken)
            {
                return GetCacheCursorAtStartPosition(consumerData.StreamId, startPositionToken, latestBoundary: null);
            }

            if (consumerData.LastToken is StartToken or DeliveryToken
                && consumerData.LastToken.Token is { } handshakeSequenceToken)
            {
                try
                {
                    if (consumerData.LastToken is DeliveryToken
                        || SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
                    {
                        return GetCursorAfterProcessedToken(consumerData, handshakeSequenceToken);
                    }

                    return GetCacheCursor(consumerData.StreamId, handshakeSequenceToken);
                }
                catch (QueueCacheMissException) when (!queueAdapter.IsRewindable)
                {
                    return queueCache!.GetCacheCursor(consumerData.StreamId, null);
                }
            }

            return queueCache!.GetCacheCursor(consumerData.StreamId, null);
        }

        private IQueueCacheCursor GetCursorAfterProcessedToken(
            StreamConsumerData consumerData,
            StreamSequenceToken processedToken)
        {
            var restartToken = consumerData.LastSafePartitionToken ?? processedToken;
            IQueueCacheCursor cursor;
            try
            {
                cursor = GetCacheCursor(consumerData.StreamId, restartToken);
            }
            catch (QueueCacheMissException) when (!queueAdapter.IsRewindable)
            {
                try
                {
                    cursor = queueCache!.GetCacheCursorAtPosition(
                        consumerData.StreamId,
                        StreamSubscriptionStartPosition.EarliestAvailable);
                }
                catch (NotSupportedException)
                {
                    cursor = queueCache!.GetCacheCursor(consumerData.StreamId, null);
                }
            }

            if (cursor is IQueueCacheCursorProgress progressCursor)
            {
                progressCursor.SetDeliveredThrough(processedToken);
            }
            else
            {
                if (!Equals(restartToken, processedToken))
                {
                    cursor.Dispose();
                    cursor = GetCacheCursor(consumerData.StreamId, processedToken);
                }

                consumerData.PendingBatch = AdvanceCursorPastToken(cursor, processedToken);
            }

            return cursor;
        }

        private IQueueCacheCursor GetCacheMissRecoveryCursor(StreamConsumerData consumerData)
        {
            try
            {
                return queueCache!.GetCacheCursorAtPosition(
                    consumerData.StreamId,
                    StreamSubscriptionStartPosition.EarliestAvailable);
            }
            catch (NotSupportedException)
            {
                return GetRecoveryCursor(consumerData);
            }
        }

        public Task RemoveSubscriber(
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveSubscriber_Impl(subscriptionId, streamId);
            return Task.CompletedTask;
        }

        public void RemoveSubscriber_Impl(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            if (IsShutdown) return;

            if (!pubSubCache.TryGetValue(streamId, out var streamData)) return;

            if (!_useLegacyDeliveryProgress && (!streamData.StreamRegistered || streamData.RegistrationTask is not null
                || streamData.TryGetConsumer(subscriptionId, out var consumer)
                    && (!HasCaughtUpDeliveryProgress(consumer, streamData.LastReadToken)
                        || _lastReadToken is null
                        || !TryCompareQueueProgress(consumer.LastProcessedToken!, _lastReadToken, out _))))
            {
                _useLegacyDeliveryProgress = true;
            }

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
                Task? localReceiverInitTask = receiverInitTask;
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                receiverInitTask = null;
            }
            catch (Exception exc)
            {
                receiverInitTask = null;
                LogErrorGivingUpReading(new(queueId), ReadLoopRetryMax, exc);
            }

            bool ReadLoopRetryExceptionFilter(Exception e, int retryCounter)
            {
                _useLegacyDeliveryProgress = true;
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
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        private async Task<bool> ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver? rcvr, int maxCacheAddCount, CancellationToken cancellationToken)
        {
            if (rcvr is null || IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            // Pause all queue reads so a cold stream's first batch stays pinned until registration completes.
            if (pubSubCache.Values.Any(static stream => stream.RegistrationTask is { IsCompleted: false }))
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            TagList? tags = null;

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

            if (queueCache is not null)
            {
                if (queueCache.TryPurgeFromCache(out var purgedItems))
                {
                    try
                    {
                        await rcvr.MessagesDeliveredAsync(purgedItems, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return false;
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

            if (queueCache is not null && queueCache.IsUnderPressure())
            {
                // Under back pressure. Exit the loop. Will attempt again in the next timer callback.
                LogInfoStreamCacheUnderPressure();
                return false;
            }

            // Retrieve one multiBatch from the queue. Every multiBatch has an IEnumerable of IBatchContainers, each IBatchContainer may have multiple events.
            IList<IBatchContainer>? multiBatch = await rcvr.GetQueueMessagesAsync(maxCacheAddCount, cancellationToken);

            // Receivers built against older Orleans versions can still return null.
            if (multiBatch is null || multiBatch.Count == 0) return false; // queue is empty. Exit the loop. Will attempt again in the next timer callback.

            var accountedFor = false;
            try
            {
                queueCache?.AddToCache(multiBatch);
                numMessages += multiBatch.Count;
                if (_streamInstruments?.PersistentStreamReadMessages.Enabled is true)
                {
                    tags = StreamInstrumentsTagUtils.InitializeTags(myQueueId, streamProviderName);
                    _streamInstruments.PersistentStreamReadMessages.Add(multiBatch.Count, tags.Value);
                }

                LogTraceGotMessages(multiBatch.Count, new(myQueueId), numMessages);

                var availableMessages = multiBatch.Where(m => m is not null).ToList();
                if (availableMessages.Count == 0)
                {
                    return false;
                }

                var partitionStartToken = availableMessages[0].SequenceToken;
                foreach (var streamData in pubSubCache.Values)
                {
                    StartInactiveCursors(streamData, partitionStartToken, CancellationToken.None);
                }

                foreach (var group in availableMessages.GroupBy(container => container.StreamId))
                {
                    var streamId = new QualifiedStreamId(queueAdapter.Name, group.Key);
                    StreamSequenceToken startToken = group.First().SequenceToken;
                    if (pubSubCache.TryGetValue(streamId, out var streamData))
                    {
                        streamData.RefreshActivity(now);
                    }
                    else
                    {
                        // Run registration in the background so that cold-stream pubsub
                        // calls do not stall message delivery for other streams on the same queue.
                        RegisterStream(streamId, startToken, now, CancellationToken.None);
                    }

                    if (pubSubCache.TryGetValue(streamId, out streamData))
                    {
                        streamData.LastReadToken = group.Last().SequenceToken;
                    }
                }

                accountedFor = true;
            }
            finally
            {
                if (!accountedFor)
                {
                    _useLegacyDeliveryProgress = true;
                }
            }

            RecordReadProgress(multiBatch);
            return !IsShutdown && !cancellationToken.IsCancellationRequested;
        }

        private void RecordReadProgress(IList<IBatchContainer> batches)
        {
            if (queueCache is null || _useLegacyDeliveryProgress) return;

            foreach (var batch in batches)
            {
                if (batch?.SequenceToken is not { } token)
                {
                    _useLegacyDeliveryProgress = true;
                    LogWarningUnknownReadProgress(new(QueueId));
                    return;
                }

                if (_lastReadToken is null)
                {
                    _lastReadToken = token;
                }
                else if (!TryCompareQueueProgress(token, _lastReadToken, out var comparison))
                {
                    return;
                }
                else if (comparison < 0)
                {
                    _useLegacyDeliveryProgress = true;
                    LogWarningUnknownReadProgress(new(QueueId));
                    return;
                }
                else
                {
                    _lastReadToken = token;
                }
            }
        }

        private void CleanupPubSubCache(DateTime now)
        {
            List<QualifiedStreamId>? inactiveStreams = null;
            foreach (var tuple in pubSubCache)
            {
                if (tuple.Value.IsInactive(now, options.StreamInactivityPeriod))
                {
                    inactiveStreams ??= new();
                    inactiveStreams.Add(tuple.Key);
                }
            }

            if (inactiveStreams is null)
            {
                return;
            }

            foreach (var streamId in inactiveStreams)
            {
                if (pubSubCache.Remove(streamId, out var streamData))
                {
                    if (!_useLegacyDeliveryProgress && (!streamData.StreamRegistered || streamData.RegistrationTask is not null
                        || streamData.AllConsumers().Any(consumer =>
                            !HasCaughtUpDeliveryProgress(consumer, streamData.LastReadToken)
                            || _lastReadToken is null
                            || !TryCompareQueueProgress(consumer.LastProcessedToken!, _lastReadToken, out _))))
                    {
                        _useLegacyDeliveryProgress = true;
                    }
                    streamData.DisposeAll(logger);
                    StreamingEvents.EmitStreamInactive(streamProviderName, streamId.StreamId, options.StreamInactivityPeriod, Silo);
                }
            }
        }

        /// <summary>
        /// Computes delivery progress so the queue can persist the latest handoff checkpoint.
        /// </summary>
        private void NotifyDeliveryProgress()
        {
            if (queueCache is null) return;

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                if (TryGetDeliveryProgress(out var earliest))
                {
                    queueCache.UpdateDeliveryProgress(earliest, utcNow);
                }
            }
            catch (ArgumentException exception)
            {
                LogWarningDeliveryProgressComparison(new(QueueId), exception);
            }
        }

        private bool TryGetDeliveryProgress(out StreamSequenceToken? earliest)
        {
            var canAdvancePastDrainedSubscriptions = !_useLegacyDeliveryProgress
                && _lastReadToken is not null
                && pubSubCache.Values.All(static stream => stream.StreamRegistered && stream.RegistrationTask is null
                    && stream.AllConsumers().All(static consumer => consumer.PendingHandshakes == 0));

            if (canAdvancePastDrainedSubscriptions
                && TryGetSubscriptionDeliveryProgress(_lastReadToken, out earliest)
                && !_useLegacyDeliveryProgress)
            {
                return true;
            }

            // Opting out restores the original calculation; it does not freeze normal delivery progress.
            return TryGetSubscriptionDeliveryProgress(null, out earliest);
        }

        private bool TryGetSubscriptionDeliveryProgress(StreamSequenceToken? readBoundary, out StreamSequenceToken? earliest)
        {
            earliest = null;
            var hasSubscriptions = false;

            foreach (var streamConsumers in pubSubCache.Values)
            {
                if (streamConsumers.RegistrationTask is { IsCompleted: false })
                {
                    return false;
                }

                foreach (var consumer in streamConsumers.AllConsumers())
                {
                    if (consumer.IsReplayUnavailable)
                    {
                        continue;
                    }

                    if (!consumer.IsRegistered || consumer.PendingHandshakes != 0 || consumer.HasUnresolvedHandshake)
                    {
                        return false;
                    }

                    if (consumer.Cursor is IQueueCacheCursorReplayState { IsReplaying: true })
                    {
                        continue;
                    }

                    var current = consumer.Cursor is IQueueCacheCursorProgress
                        ? consumer.LastSafePartitionToken
                        : consumer.LastProcessedToken;
                    if (consumer.Cursor is not IQueueCacheCursorProgress
                        && consumer.LastSafePartitionToken is { } safePartition
                        && (current is null || IsBefore(current, safePartition)))
                    {
                        current = safePartition;
                    }

                    if (current is null)
                    {
                        return false;
                    }

                    hasSubscriptions = true;
                    if (readBoundary is not null)
                    {
                        if (!TryCompareQueueProgress(current, readBoundary, out _))
                        {
                            return false;
                        }

                        if (HasCaughtUpDeliveryProgress(consumer, streamConsumers.LastReadToken))
                        {
                            continue;
                        }
                    }

                    if (earliest is null)
                    {
                        earliest = current;
                        continue;
                    }

                    try
                    {
                        if (IsBefore(current, earliest))
                        {
                            earliest = current;
                        }
                    }
                    catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "other")
                    {
                        LogWarningIncompatibleDeliveryProgress(
                            new(QueueId), consumer.SubscriptionId, consumer.StreamId, current, earliest, exception);
                        earliest = null;
                        return false;
                    }
                }
            }

            if (readBoundary is not null && hasSubscriptions)
            {
                if (earliest is null)
                {
                    earliest = readBoundary;
                }
                else
                {
                    if (!TryCompareQueueProgress(readBoundary, earliest, out var comparison))
                    {
                        return false;
                    }

                    if (comparison < 0)
                    {
                        earliest = readBoundary;
                    }
                }
            }

            return true;
        }

        private bool HasCaughtUpDeliveryProgress(StreamConsumerData consumer, StreamSequenceToken? streamReadBoundary)
            => consumer.IsCaughtUp
                && consumer.State == StreamConsumerDataState.Inactive
                && consumer.PendingHandshakes == 0
                && !consumer.HasUnresolvedHandshake
                && consumer.PendingBatch is null
                && consumer.Cursor is not null
                && consumer.LastProcessedToken is { } progress
                && streamReadBoundary is not null
                && TryCompareQueueProgress(progress, streamReadBoundary, out var comparison)
                && comparison >= 0;

        private bool TryCompareQueueProgress(StreamSequenceToken token, StreamSequenceToken other, out int comparison)
        {
            try
            {
                comparison = EventSequenceTokenCompatibility.Compare(token, other);
                return true;
            }
            catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "other")
            {
                _useLegacyDeliveryProgress = true;
                LogWarningIncompatibleQueueProgress(new(QueueId), token, other, exception);
                comparison = default;
                return false;
            }
        }

        private void ScheduleDeliveryProgressUpdate()
        {
            this.RunOrQueueTask(() =>
                {
                    if (!IsShutdown && deliveryProgressTimer is not null)
                    {
                        NotifyDeliveryProgress();
                    }

                    return Task.CompletedTask;
                })
                .LogException(
                    logger,
                    ErrorCode.PersistentStreamPullingAgent_28,
                    $"Failed to update delivery progress for queue {QueueId}.")
                .Ignore();
        }

        private static bool IsBefore(StreamSequenceToken current, StreamSequenceToken other)
            => EventSequenceTokenCompatibility.Compare(current, other) < 0;

        private IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
        {
            try
            {
                return queueCache!.GetCacheCursor(streamId, token);
            }
            catch (ArgumentException exception) when (queueAdapter.IsRewindable && token is not null)
            {
                throw new DataNotAvailableException(
                    $"The requested token '{token}' is not valid for stream '{streamId}' in provider '{streamProviderName}'.",
                    exception);
            }
        }

        private static void UpdateCursorProgress(
            StreamConsumerData consumerData,
            IQueueCacheCursorProgress? progressCursor)
        {
            if (progressCursor?.SafeSequenceToken is not { } safeToken)
            {
                return;
            }

            if (consumerData.LastSafePartitionToken is null
                || IsBefore(consumerData.LastSafePartitionToken, safeToken))
            {
                consumerData.LastSafePartitionToken = safeToken;
            }
        }

        private void RegisterStream(
            QualifiedStreamId streamId,
            StreamSequenceToken firstToken,
            DateTime now,
            CancellationToken cancellationToken)
        {
            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var workAdmission = _workAdmission;
            if (!workAdmission.TryEnterUnscoped())
            {
                return;
            }

            var streamData = new StreamConsumerCollection(now);

            // Create a fake cursor to point into a cache.
            // That way we will not purge the event from the cache, until we talk to pub sub.
            // This will help ensure the "casual consistency" between pre-existing subscripton (of a potentially new already subscribed consumer)
            // and later production.
            IQueueCacheCursor? pinCursor;
            try
            {
                pinCursor = queueCache?.GetCacheCursor(streamId, firstToken);
            }
            catch
            {
                workAdmission.Exit();
                throw;
            }

            streamData.RegistrationTask = RegisterStreamAsync();
            pubSubCache.Add(streamId, streamData);

            async Task RegisterStreamAsync()
            {
                try
                {
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

                    if (IsShutdown || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var subscribers = await RegisterAsStreamProducer(streamId, cancellationToken);

                    if (IsShutdown || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Producer registration succeeded; the stream entry is now established.
                    // Subscriber-handshake failures must not tear down the entry from here on.
                    streamData.StreamRegistered = true;
                    StreamingEvents.EmitPullingAgentStreamRegistered(streamProviderName, Silo, QueueId, streamId.StreamId, GrainId, subscribers.Count);

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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RemoveCanceledRegistration();
                }
                catch (Exception exception)
                {
                    FailRegistration(exception);
                }
                finally
                {
                    try
                    {
                        streamData.RegistrationTask = null;
                        pinCursor?.Dispose();
                    }
                    finally
                    {
                        workAdmission.Exit();
                    }
                }
            }

            void RemoveCanceledRegistration()
            {
                _useLegacyDeliveryProgress = true;
                if (pubSubCache.TryGetValue(streamId, out var cachedStreamData)
                    && ReferenceEquals(cachedStreamData, streamData))
                {
                    pubSubCache.Remove(streamId);
                }

                streamData.DisposeAll(logger);
            }

            void FailRegistration(Exception exception)
            {
                _useLegacyDeliveryProgress = true;
                LogWarningFailedToRegisterStream(streamId, exception);
                StreamingEvents.EmitPullingAgentStreamRegistrationFailed(streamProviderName, Silo, QueueId, streamId.StreamId, exception);

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
                if (IsShutdown || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await AddSubscriber_Impl(item.SubscriptionId, item.Stream, item.Consumer, item.FilterData, firstToken);
                }
                catch (Exception exception)
                {
                    _useLegacyDeliveryProgress = true;
                    LogWarningFailedToAddSubscription(item.Stream, exception);
                }
            }
        }

        private void StartInactiveCursors(StreamConsumerCollection streamData, StreamSequenceToken startToken, CancellationToken cancellationToken)
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
                        RunConsumerCursor(consumerData, cancellationToken).Ignore();
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

        private async Task RunConsumerCursor(StreamConsumerData consumerData, CancellationToken cancellationToken = default)
        {
            using var admission = _workAdmission.TryEnter();
            if (!admission.Entered || IsShutdown) return;

            TagList? tags = null;
            try
            {
                // double check in case of interleaving
                if (consumerData.State == StreamConsumerDataState.Active ||
                    consumerData.Cursor is null) return;

                consumerData.State = StreamConsumerDataState.Active;
                consumerData.IsCaughtUp = false;
                var deliveredAny = false;
                var replayRetryAttempt = 0;
                while (!IsShutdown && !cancellationToken.IsCancellationRequested && consumerData.Cursor is not null)
                {
                    var handshakeGeneration = consumerData.HandshakeGeneration;
                    var activeCursor = consumerData.Cursor;
                    var progressCursor = activeCursor as IQueueCacheCursorProgress;
                    var batchCursor = options.BatchContainerBatchSize > 1
                        ? activeCursor as IQueueCacheCursorBatchDelivery
                        : null;
                    using var deliveryBatch = batchCursor?.ProtectDeliveryBatch();
                    ConsumerBatch nextBatch = default;
                    Exception? exceptionOccured = null;
                    var forceFaultSubscription = false;
                    try
                    {
                        var pendingBatch = GetBatchForConsumer(
                            consumerData,
                            activeCursor,
                            cancellationToken);
                        nextBatch = pendingBatch.IsCompletedSuccessfully
                            ? pendingBatch.Result
                            : await pendingBatch;
                        if (!ReferenceEquals(consumerData.Cursor, activeCursor))
                        {
                            consumerData.State = StreamConsumerDataState.Inactive;
                            return;
                        }
                        replayRetryAttempt = 0;
                        replayRetryAttempt = 0;
                        UpdateCursorProgress(consumerData, progressCursor);
                        if (nextBatch.ShouldRetry)
                        {
                            _useLegacyDeliveryProgress = true;
                            continue;
                        }

                        if (!nextBatch.HasProgress)
                        {
                            if (nextBatch.CursorResult.Kind == QueueCacheCursorMoveResultKind.NoData)
                            {
                                consumerData.IsCaughtUp = consumerData.LastProcessedToken is not null;
                            }
                            else
                            {
                                _useLegacyDeliveryProgress = true;
                            }

                            // Only emit cursor-drained when we transitioned from delivering to empty,
                            // not on every empty poll.
                            if (deliveredAny)
                                StreamingEvents.EmitConsumerCursorDrained(streamProviderName, consumerData.StreamId.StreamId, consumerData.SubscriptionId.Guid, Silo);
                            break;
                        }
                    }
                    catch (QueueCacheMissException exc)
                    {
                        _useLegacyDeliveryProgress = true;
                        exceptionOccured = exc;
                        consumerData.SafeDisposeCursor(logger);
                        consumerData.Cursor = GetCacheMissRecoveryCursor(consumerData);
                    }
                    catch (Exception exc)
                    {
                        if (!ReferenceEquals(consumerData.Cursor, activeCursor))
                        {
                            consumerData.State = StreamConsumerDataState.Inactive;
                            return;
                        }

                        exceptionOccured = exc;
                        var wasReplaying = activeCursor is IQueueCacheCursorReplayState { IsReplaying: true };
                        consumerData.SafeDisposeCursor(logger);
                        consumerData.IsReplayUnavailable = wasReplaying && exc is DataNotAvailableException;
                        if (wasReplaying && exc is TransientStreamReplayException)
                        {
                            var resumeToken = consumerData.LastSafePartitionToken
                                ?? consumerData.CursorStartToken;
                            if (resumeToken is not null)
                            {
                                try
                                {
                                    var retryCursor = GetCacheCursor(consumerData.StreamId, resumeToken);
                                    if (consumerData.LastSafePartitionToken is { } deliveredThrough
                                        && retryCursor is IQueueCacheCursorProgress retryProgress)
                                    {
                                        retryProgress.SetDeliveredThrough(deliveredThrough);
                                    }

                                    consumerData.Cursor = retryCursor;
                                }
                                catch (DataNotAvailableException retryException)
                                {
                                    exceptionOccured = retryException;
                                    consumerData.IsReplayUnavailable = true;
                                }
                            }
                        }
                        else if (!wasReplaying)
                        {
                            consumerData.Cursor = GetRecoveryCursor(consumerData);
                        }
                    }

                    if (exceptionOccured is not null)
                    {
                        var faultedSubscription = await ErrorProtocol(
                            consumerData,
                            exceptionOccured,
                            false,
                            null,
                            consumerData.CursorStartToken,
                            cancellationToken: cancellationToken);
                        if (faultedSubscription || consumerData.Cursor is null)
                        {
                            consumerData.State = StreamConsumerDataState.Inactive;
                            return;
                        }

                        if (exceptionOccured is TransientStreamReplayException)
                        {
                            var retryDelay = deliveryBackoffProvider.Next(replayRetryAttempt++);
                            if (retryDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(retryDelay, _timeProvider, cancellationToken);
                            }
                        }
                        continue;
                    }

                    if (exceptionOccured is null)
                    {
                        deliveredAny = true;

                        if (nextBatch.Batch is null)
                        {
                            progressCursor?.RecordDeliverySuccess();
                            consumerData.LastProcessedToken = nextBatch.ProgressToken;
                            UpdateCursorProgress(consumerData, progressCursor);
                            continue;
                        }
                    }

                    try
                    {
                        if (_streamInstruments?.PersistentStreamSentMessages.Enabled is true)
                        {
                            tags ??= StreamInstrumentsTagUtils.InitializeTags(
                                consumerData.StreamId, consumerData.StreamConsumer.GetGrainId());
                            _streamInstruments.PersistentStreamSentMessages.Add(1, tags.Value);
                        }

                        if (IsShutdown)
                        {
                            break;
                        }

                        if (nextBatch.Batch is not null)
                        {
                            var batch = nextBatch.Batch;
                            var handshakeToken = consumerData.StartPositionIsProviderDefault ? null : consumerData.LastToken;
                            StreamHandshakeToken? newToken = await AsyncExecutorWithRetries.ExecuteWithRetries(
                                i => DeliverBatchToConsumer(consumerData, batch, handshakeToken, cancellationToken),
                                AsyncExecutorWithRetries.INFINITE_RETRIES,
                                // Do not retry if the agent is shutting down, or if the exception is ClientNotAvailableException
                                (exception, i) => exception is not ClientNotAvailableException && !IsShutdown
                                    && handshakeGeneration == consumerData.HandshakeGeneration,
                                this.options.MaxEventDeliveryTime,
                                deliveryBackoffProvider,
                                cancellationToken: cancellationToken);

                            // A completed handshake owns its replacement position, including pending replay.
                            if (handshakeGeneration != consumerData.HandshakeGeneration)
                            {
                                continue;
                            }

                            consumerData.LastToken = StreamHandshakeToken.CreateDeliveyToken(batch.SequenceToken);
                            consumerData.StartPositionIsProviderDefault = false;
                            if (newToken is not null)
                            {
                                _useLegacyDeliveryProgress = true;
                                var previousSafePartitionToken = consumerData.LastSafePartitionToken;
                                consumerData.LastToken = newToken;
                                IQueueCacheCursor newCursor;
                                IBatchContainer? pendingBatch = null;
                                var resumedFromFallback = false;
                                if (newToken is StartPositionToken startPositionToken)
                                {
                                    consumerData.LastProcessedToken = null;
                                    try
                                    {
                                        newCursor = GetCacheCursorAtStartPosition(
                                            consumerData.StreamId,
                                            startPositionToken,
                                            batch.SequenceToken);
                                    }
                                    catch (NotSupportedException)
                                    {
                                        forceFaultSubscription = true;
                                        throw;
                                    }
                                }
                                else if (newToken is StartToken)
                                {
                                    var sequenceToken = newToken.Token
                                        ?? throw new InvalidOperationException("A start handshake token must contain a stream sequence token.");
                                    if (SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
                                    {
                                        consumerData.LastProcessedToken = sequenceToken;
                                        try
                                        {
                                            newCursor = GetCacheCursor(consumerData.StreamId, sequenceToken);
                                            // An implicit recovery token identifies the last event processed by the prior activation.
                                            if (newCursor is IQueueCacheCursorProgress progress)
                                            {
                                                progress.SetDeliveredThrough(sequenceToken);
                                            }
                                            else
                                            {
                                                pendingBatch = AdvanceCursorPastToken(newCursor, sequenceToken);
                                            }
                                        }
                                        catch (QueueCacheMissException) when (!queueAdapter.IsRewindable)
                                        {
                                            // The current batch is the receiver's first available message.
                                            // Keep it pending when the prior activation's token was evicted.
                                            newCursor = GetCacheCursor(consumerData.StreamId, batch.SequenceToken);
                                            resumedFromFallback = true;
                                        }
                                    }
                                    else
                                    {
                                        consumerData.LastProcessedToken = null;
                                        newCursor = GetCacheCursor(consumerData.StreamId, sequenceToken);
                                    }
                                }
                                else if (newToken is DeliveryToken)
                                {
                                    var sequenceToken = newToken.Token
                                        ?? throw new InvalidOperationException("A delivery handshake token must contain a stream sequence token.");
                                    consumerData.LastProcessedToken = sequenceToken;
                                    try
                                    {
                                        var restartToken = previousSafePartitionToken ?? sequenceToken;
                                        newCursor = GetCacheCursor(consumerData.StreamId, restartToken);
                                        if (newCursor is IQueueCacheCursorProgress progress)
                                        {
                                            progress.SetDeliveredThrough(sequenceToken);
                                        }
                                        else
                                        {
                                            if (!Equals(restartToken, sequenceToken))
                                            {
                                                newCursor.Dispose();
                                                newCursor = GetCacheCursor(consumerData.StreamId, sequenceToken);
                                            }

                                            pendingBatch = AdvanceCursorPastToken(newCursor, sequenceToken);
                                        }
                                    }
                                    catch (QueueCacheMissException) when (!queueAdapter.IsRewindable)
                                    {
                                        // The current batch is the receiver's first available message.
                                        // Keep it pending when the consumer resumes from an evicted token.
                                        newCursor = GetCacheCursor(consumerData.StreamId, batch.SequenceToken);
                                        resumedFromFallback = true;
                                    }
                                }
                                else
                                {
                                    forceFaultSubscription = true;
                                    throw new InvalidOperationException($"Unsupported stream handshake token type {newToken.GetType().FullName}.");
                                }

                                consumerData.SafeDisposeCursor(logger);
                                consumerData.Cursor = newCursor;
                                consumerData.PendingBatch = pendingBatch;
                                consumerData.CursorStartToken = resumedFromFallback ? batch.SequenceToken : newToken.Token;
                                if (newToken is DeliveryToken deliveryToken)
                                {
                                    consumerData.LastProcessedToken = deliveryToken.Token;
                                    consumerData.LastSafePartitionToken = previousSafePartitionToken;
                                    UpdateCursorProgress(consumerData, newCursor as IQueueCacheCursorProgress);
                                }
                                else
                                {
                                    consumerData.LastProcessedToken = null;
                                    consumerData.LastSafePartitionToken = null;
                                }
                            }
                            else
                            {
                                // Track progress for the periodic delivery scan.
                                progressCursor?.RecordDeliverySuccess();
                                consumerData.LastProcessedToken = nextBatch.ProgressToken;
                                UpdateCursorProgress(consumerData, progressCursor);
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        _useLegacyDeliveryProgress = true;
                        LogErrorDeliveringMessages(consumerData.StreamId, exc);
                        if (handshakeGeneration != consumerData.HandshakeGeneration)
                        {
                            continue;
                        }

                        if (batchCursor is not null && nextBatch.Batch is not null)
                        {
                            batchCursor.RecordDeliveryFailure(nextBatch.Batch);
                        }
                        else
                        {
                            consumerData.Cursor?.RecordDeliveryFailure();
                        }

                        exceptionOccured = exc is ClientNotAvailableException || forceFaultSubscription
                            ? exc
                            : new StreamEventDeliveryFailureException(consumerData.StreamId);
                    }
                    // if we failed to deliver a batch
                    if (exceptionOccured is not null)
                    {
                        var batch = nextBatch.Batch;
                        bool faultedSubscription = await ErrorProtocol(
                            consumerData,
                            exceptionOccured,
                            true,
                            batch,
                            batch?.SequenceToken,
                            handshakeGeneration,
                            forceFaultSubscription,
                            cancellationToken);
                        if (faultedSubscription) return;
                    }
                }
                consumerData.State = StreamConsumerDataState.Inactive;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _useLegacyDeliveryProgress = true;
                consumerData.State = StreamConsumerDataState.Inactive;
            }
            catch (Exception exc)
            {
                _useLegacyDeliveryProgress = true;
                // RunConsumerCursor is fired with .Ignore so we should log if anything goes wrong, because there is no one to catch the exception
                LogErrorRunConsumerCursor(exc);
                consumerData.State = StreamConsumerDataState.Inactive;
                throw;
            }
        }

        private readonly struct ConsumerBatch
        {
            public ConsumerBatch(
                IBatchContainer? batch,
                StreamSequenceToken? progressToken,
                bool shouldRetry = false)
            {
                Batch = batch;
                ProgressToken = progressToken;
                ShouldRetry = shouldRetry;
            }

            public IBatchContainer? Batch { get; }
            public StreamSequenceToken? ProgressToken { get; }
            public bool HasProgress => ProgressToken is not null;
            public bool ShouldRetry { get; }
        }

        private ValueTask<ConsumerBatch> GetBatchForConsumer(
            StreamConsumerData consumerData,
            IQueueCacheCursor cursor,
            CancellationToken cancellationToken)
            => cursor is IAsyncQueueCacheCursor asyncCursor
                ? GetBatchForConsumerAsync(
                    asyncCursor,
                    consumerData.StreamId.StreamId,
                    consumerData.FilterData,
                    cancellationToken)
                : ValueTask.FromResult(GetBatchForConsumerSynchronously(consumerData, cursor));

        private ConsumerBatch GetBatchForConsumerSynchronously(
            StreamConsumerData consumerData,
            IQueueCacheCursor cursor)
        {
            var streamId = consumerData.StreamId.StreamId;
            var filterData = consumerData.FilterData;
            var pendingBatch = consumerData.PendingBatch;
            consumerData.PendingBatch = null;

            if (this.options.BatchContainerBatchSize <= 1)
            {
                IBatchContainer batchContainer;
                if (pendingBatch is not null)
                {
                    batchContainer = pendingBatch;
                }
                else if (cursor.MoveNext())
                {
                    batchContainer = cursor.GetCurrent(out _)!; // MoveNext returned true, so GetCurrent is guaranteed non-null here.
                }
                else
                {
                    return default;
                }

                return ShouldDeliverBatch(streamId, batchContainer, filterData)
                    ? new ConsumerBatch(batchContainer, batchContainer.SequenceToken)
                    : new ConsumerBatch(null, batchContainer.SequenceToken);
            }
            else if (this.options.BatchContainerBatchSize > 1)
            {
                int i = 0;
                var batchContainers = new List<IBatchContainer>();
                StreamSequenceToken? progressToken = null;

                if (pendingBatch is not null)
                {
                    progressToken = pendingBatch.SequenceToken;
                    if (ShouldDeliverBatch(streamId, pendingBatch, filterData))
                    {
                        batchContainers.Add(pendingBatch);
                        i++;
                    }
                }

                while (i < this.options.BatchContainerBatchSize)
                {
                    if (!cursor.MoveNext())
                    {
                        break;
                    }

                    var batchContainer = cursor.GetCurrent(out _)!; // MoveNext returned true, so GetCurrent is guaranteed non-null here.
                    progressToken = batchContainer.SequenceToken;

                    if (!ShouldDeliverBatch(streamId, batchContainer, filterData))
                        continue;

                    batchContainers.Add(batchContainer);
                    i++;
                }

                if (progressToken is null)
                {
                    return default;
                }

                return i == 0
                    ? new ConsumerBatch(null, progressToken)
                    : new ConsumerBatch(new BatchContainerBatch(batchContainers), progressToken);
            }

            return default;
        }

        private async ValueTask<ConsumerBatch> GetBatchForConsumerAsync(
            IAsyncQueueCacheCursor cursor,
            StreamId streamId,
            string? filterData,
            CancellationToken cancellationToken)
        {
            if (this.options.BatchContainerBatchSize <= 1)
            {
                var result = await cursor.MoveNextAsync(cancellationToken);
                if (result == QueueCacheCursorMoveNextResult.TemporaryTail)
                {
                    return new ConsumerBatch(null, null, shouldRetry: true);
                }

                if (result == QueueCacheCursorMoveNextResult.Completed)
                {
                    return cursor is IQueueCacheCursorReplayState { HasPendingLiveHandoff: true }
                        ? new ConsumerBatch(null, null, shouldRetry: true)
                        : default;
                }

                var batchContainer = cursor.GetCurrent(out var exception);
                if (exception is not null)
                {
                    throw exception;
                }

                if (batchContainer is null)
                {
                    throw new InvalidOperationException("An asynchronous queue cache cursor advanced without a current record.");
                }

                return ShouldDeliverBatch(streamId, batchContainer, filterData)
                    ? new ConsumerBatch(batchContainer, batchContainer.SequenceToken)
                    : new ConsumerBatch(null, batchContainer.SequenceToken);
            }

            if (this.options.BatchContainerBatchSize > 1)
            {
                var batchContainers = new List<IBatchContainer>();
                StreamSequenceToken? progressToken = null;
                var handoffPending = false;
                while (batchContainers.Count < this.options.BatchContainerBatchSize)
                {
                    var result = await cursor.MoveNextAsync(cancellationToken);
                    if (result == QueueCacheCursorMoveNextResult.TemporaryTail)
                    {
                        return progressToken is null
                            ? new ConsumerBatch(null, null, shouldRetry: true)
                            : batchContainers.Count == 0
                                ? new ConsumerBatch(null, progressToken)
                                : new ConsumerBatch(new BatchContainerBatch(batchContainers), progressToken);
                    }

                    if (result == QueueCacheCursorMoveNextResult.Completed)
                    {
                        handoffPending = cursor is IQueueCacheCursorReplayState { HasPendingLiveHandoff: true };
                        break;
                    }

                    var batchContainer = cursor.GetCurrent(out var exception);
                    if (exception is not null)
                    {
                        throw exception;
                    }

                    if (batchContainer is null)
                    {
                        throw new InvalidOperationException("An asynchronous queue cache cursor advanced without a current record.");
                    }

                    progressToken = batchContainer.SequenceToken;
                    if (ShouldDeliverBatch(streamId, batchContainer, filterData))
                    {
                        batchContainers.Add(batchContainer);
                    }

                    if (cursor is IQueueCacheCursorReplayState { IsReplaying: true })
                    {
                        break;
                    }
                }

                if (progressToken is null)
                {
                    return handoffPending
                        ? new ConsumerBatch(null, null, shouldRetry: true)
                        : default;
                }

                return batchContainers.Count == 0
                    ? new ConsumerBatch(null, progressToken)
                    : new ConsumerBatch(new BatchContainerBatch(batchContainers), progressToken);
            }

            return default;
        }

        private async Task<StreamHandshakeToken?> DeliverBatchToConsumer(
            StreamConsumerData consumerData,
            IBatchContainer batch,
            StreamHandshakeToken? handshakeToken,
            CancellationToken cancellationToken)
        {
            try
            {
                StreamHandshakeToken? newToken = await ContextualizedDeliverBatchToConsumer(consumerData, batch, handshakeToken, cancellationToken);
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
        private static Task<StreamHandshakeToken?> ContextualizedDeliverBatchToConsumer(
            StreamConsumerData consumerData,
            IBatchContainer batch,
            StreamHandshakeToken? handshakeToken,
            CancellationToken cancellationToken)
        {
            bool isRequestContextSet = batch.ImportRequestContext();
            try
            {
                return consumerData.StreamConsumer.DeliverBatch(
                    consumerData.SubscriptionId,
                    consumerData.StreamId,
                    batch,
                    handshakeToken,
                    cancellationToken);
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


        private static async Task DeliverErrorToConsumer(
            StreamConsumerData consumerData,
            Exception exc,
            IBatchContainer? batch,
            CancellationToken cancellationToken)
        {
            Task errorDeliveryTask;
            bool isRequestContextSet = batch != null && batch.ImportRequestContext();
            try
            {
                errorDeliveryTask = consumerData.StreamConsumer.ErrorInStream(consumerData.SubscriptionId, exc, cancellationToken);
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

        private async Task<bool> ErrorProtocol(
            StreamConsumerData consumerData,
            Exception exceptionOccured,
            bool isDeliveryError,
            IBatchContainer? batch,
            StreamSequenceToken? token,
            long operationId,
            bool forceFaultSubscription = false,
            CancellationToken cancellationToken = default)
        {
            if (!IsCurrent()) return false;

            // for loss of client, we just remove the subscription
            if (exceptionOccured is ClientNotAvailableException)
            {
                LogWarningConsumerIsDead(consumerData.StreamConsumer, consumerData.StreamId);
                await pubSub.UnregisterConsumer(consumerData.SubscriptionId, consumerData.StreamId, CancellationToken.None);
                return true;
            }

            // notify consumer about the error or that the data is not available.
            await DeliverErrorToConsumer(consumerData, exceptionOccured, batch, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (!IsCurrent()) return false;

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

            if (!IsCurrent()) return false;

            // if configured to fault on delivery failure and this is not an implicit subscription, fault and remove the subscription
            if ((forceFaultSubscription || streamFailureHandler.ShouldFaultSubsriptionOnError)
                && !SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
            {
                var faultRequested = false;
                try
                {
                    // notify consumer of faulted subscription, if we can.
                    await DeliverErrorToConsumer(
                        consumerData,
                        new FaultedSubscriptionException(consumerData.SubscriptionId, consumerData.StreamId),
                        batch,
                        cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                    if (!IsCurrent()) return false;

                    // mark subscription as faulted.
                    faultRequested = true;
                    await pubSub.FaultSubscription(consumerData.StreamId, consumerData.SubscriptionId, cancellationToken);
                }
                finally
                {
                    if (faultRequested)
                    {
                        RemoveSubscriber_Impl(consumerData.SubscriptionId, consumerData.StreamId);
                    }
                }
                return true;
            }
            return false;

            bool IsCurrent()
                => operationId == (isDeliveryError ? consumerData.HandshakeGeneration : consumerData.HandshakeRequestId);
        }

        private static async Task<ISet<PubSubSubscriptionState>> PubsubRegisterProducer(
            IStreamPubSub pubSub,
            QualifiedStreamId streamId,
            GrainId meAsStreamProducer,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            try
            {
                var streamData = await pubSub.RegisterProducer(streamId, meAsStreamProducer, cancellationToken);
                return streamData;
            }
            catch (Exception e)
            {
                LogErrorRegisterAsStreamProducer(logger, e);
                throw;
            }
        }

        private async Task<ISet<PubSubSubscriptionState>> RegisterAsStreamProducer(
            QualifiedStreamId streamId,
            CancellationToken cancellationToken)
        {
            if (pubSub == null) throw new NullReferenceException("Found pubSub reference not set up correctly in RetrieveNewStream");
            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return new HashSet<PubSubSubscriptionState>();
            }

            var subscribers = await AsyncExecutorWithRetries.ExecuteWithRetries(
                i => PubsubRegisterProducer(pubSub, streamId, GrainId, logger, cancellationToken),
                AsyncExecutorWithRetries.INFINITE_RETRIES,
                (exception, i) => !IsShutdown && !cancellationToken.IsCancellationRequested,
                Timeout.InfiniteTimeSpan,
                deliveryBackoffProvider,
                cancellationToken: cancellationToken);

            return subscribers;
        }

        private bool ShouldDeliverBatch(StreamId streamId, IBatchContainer batchContainer, string? filterData)
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

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Cannot establish the read boundary for queue {QueueId}; using subscription-based delivery progress.")]
        private partial void LogWarningUnknownReadProgress(QueueIdLogRecord queueId);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Incompatible tokens {Token} and {Other} for queue {QueueId}; using subscription-based delivery progress.")]
        private partial void LogWarningIncompatibleQueueProgress(
            QueueIdLogRecord queueId, StreamSequenceToken token, StreamSequenceToken other, Exception exception);

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
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_28,
            Message = "Unable to compare delivery progress tokens for queue {QueueId}. The checkpoint will not advance."
        )]
        private partial void LogWarningDeliveryProgressComparison(QueueIdLogRecord queueId, Exception exception);

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
            Message = "Retaining the previous delivery watermark for queue {Queue}: subscription {SubscriptionId} on stream {StreamId} has token {Token} incompatible with {OtherToken}."
        )]
        private partial void LogWarningIncompatibleDeliveryProgress(
            QueueIdLogRecord queue,
            GuidId subscriptionId,
            QualifiedStreamId streamId,
            StreamSequenceToken token,
            StreamSequenceToken otherToken,
            Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_13,
            Message = "Ignoring exception while trying to evaluate subscription filter '{Filter}' with data '{FilterData}' on stream {StreamId}"
        )]
        private partial void LogWarningFilterEvaluation(string filter, string? filterData, StreamId streamId, Exception exception);
    }
}
