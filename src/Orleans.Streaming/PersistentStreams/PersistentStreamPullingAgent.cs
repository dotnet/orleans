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
        // Like queue reads, automatic recovery stays finite even when the delivery timeout is unlimited.
        private const int RecoveryAttemptMax = 6;
        private const int StreamInactivityCheckFrequency = 10;
        private readonly IBackoffProvider deliveryBackoffProvider;
        private readonly IBackoffProvider queueReaderBackoffProvider;
        private readonly string streamProviderName;
        private readonly IStreamPubSub pubSub;
        private readonly IStreamFilter streamFilter;
        private readonly Dictionary<QualifiedStreamId, StreamConsumerCollection> pubSubCache;
        private readonly HashSet<StreamConsumerData> pendingConsumerRecoveries = new();
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

        private Task? receiverInitTask;
        private Task _activePumpTask = Task.CompletedTask;
        private StreamSequenceToken? _lastReadToken;
        private StreamSequenceToken? _retainedDeliveryProgress;
        private bool _hasUnknownReplayPosition;
        private bool IsShutdown => timer is null;
        private string StatisticUniquePostfix => $"{streamProviderName}.{QueueId}";

        internal interface ITestAccessor
        {
            Task<bool> ReadFromQueue(QueueId myQueueId, IQueueAdapterReceiver? receiver, int maxCacheAddCount);
            Task<bool> ReadFromQueueWithCancellation(QueueId myQueueId, IQueueAdapterReceiver? receiver, int maxCacheAddCount, CancellationToken cancellationToken);
            Task RegisterStream(QualifiedStreamId streamId, StreamSequenceToken firstToken, DateTime now);
            Task<IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>> GetPubSubCache();
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

        Task<IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>> ITestAccessor.GetPubSubCache()
            => this.RunOrQueueTaskResult(() => (IReadOnlyDictionary<QualifiedStreamId, StreamConsumerCollection>)new Dictionary<QualifiedStreamId, StreamConsumerCollection>(pubSubCache));

        Task<bool> ITestAccessor.DoHandshakeWithConsumer(StreamConsumerData consumerData, StreamSequenceToken? cacheToken)
            => this.RunOrQueueTaskResult(() => DoHandshakeWithConsumer(consumerData, cacheToken)).Unwrap();

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
        public Task Initialize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogInfoInit(GetType().Name, GrainId, Silo, new(QueueId));

            _activePumpTask = Task.CompletedTask;
            // Progress belongs to this receiver lifetime, not the reusable pulling-agent instance.
            _lastReadToken = null;
            _retainedDeliveryProgress = null;
            _hasUnknownReplayPosition = false;
            pendingConsumerRecoveries.Clear();
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
            StreamingEvents.EmitPullingAgentStarted(streamProviderName, Silo, QueueId, randomTimerOffset, this.options.GetQueueMsgsTimerPeriod);

            _streamInstruments?.RegisterPersistentStreamPubSubCacheSizeObserve(() => new Measurement<int>(pubSubCache.Count, new KeyValuePair<string, object?>("name", StatisticUniquePostfix)));

            LogInfoTakingQueue(new(QueueId));
            return Task.CompletedTask;

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

        public async Task Shutdown(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Stop pulling from queues that are not in my range anymore.
            LogInfoShutdown(GetType().Name, new(QueueId));

            var asyncTimer = timer;
            timer = null;
            if (asyncTimer is not null)
            {
                asyncTimer.Dispose();
                StreamingEvents.EmitPullingAgentStopped(streamProviderName, Silo, QueueId);
            }

            Task? localReceiverInitTask = receiverInitTask;
            if (localReceiverInitTask != null)
            {
                await localReceiverInitTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                receiverInitTask = null;
            }

            await _activePumpTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

            // Final delivery progress scan so the receiver has the latest watermark
            // before FlushAsync persists the checkpoint.
            NotifyDeliveryProgress();

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

            // Drain any in-progress background registration tasks before proceeding.
            // Setting timer = null above makes IsShutdown = true, which causes registrations
            // to stop retrying, so these tasks will complete quickly.
            var inFlightRegistrations = pubSubCache.Values
                .Select(v => v.RegistrationTask)
                .OfType<Task>()
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
            pendingConsumerRecoveries.Clear();
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
            if (IsShutdown) return;

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
            data.PendingStartToken ??= cacheToken;
            data.PendingHandshakes++;
            var attached = false;
            try
            {
                if (await DoHandshakeWithConsumer(data, cacheToken))
                {
                    RecordDeliveryProgress(data, GetInitialDeliveryProgress(data.LastToken, data.LastProcessedToken));
                    if (!data.HasDeliveryProgressError && data.LastProcessedToken is not null)
                    {
                        data.PendingStartToken = null;
                    }
                    data.IsRegistered = true;
                    attached = true;
                    StreamingEvents.EmitSubscriptionAttached(streamProviderName, streamId.StreamId, subscriptionId.Guid, streamConsumer, Silo);
                }
            }
            finally
            {
                data.PendingHandshakes--;
            }

            if (attached && data.PendingHandshakes == 0 && data.State == StreamConsumerDataState.Inactive)
            {
                RunConsumerCursor(data, useCurrentCursor: true).Ignore();
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
            StreamSequenceToken? cacheToken)
        {
            if (IsShutdown || consumerData.IsRemoved) return false;

            StreamHandshakeToken? requestedHandshakeToken = null;
            StreamHandshakeToken? effectiveHandshakeToken = null;
            var providerDefaultRequest = false;
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
                         (exception, i) => exception is not ClientNotAvailableException && !IsShutdown && !consumerData.IsRemoved,
                         this.options.MaxEventDeliveryTime,
                         deliveryBackoffProvider,
                         cancellationToken: CancellationToken.None);

                    if (IsShutdown || consumerData.IsRemoved) return false;
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
                            RecordReplayRequest(consumerData, startPositionToken);
                            RecordDeliveryProgress(consumerData, null);
                            consumerData.SafeDisposeCursor(logger);
                            consumerData.Cursor = GetCacheCursorAtStartPosition(
                                consumerData.StreamId,
                                startPositionToken,
                                cacheToken ?? consumerData.PendingStartToken);
                        }
                    }
                    else if (effectiveHandshakeToken is StartToken or DeliveryToken
                        && effectiveHandshakeToken.Token is { } requestedToken)
                    {
                        RecordReplayRequest(consumerData, effectiveHandshakeToken);
                        RecordDeliveryProgress(
                            consumerData, GetInitialDeliveryProgress(effectiveHandshakeToken, consumerData.LastProcessedToken));
                        consumerData.SafeDisposeCursor(logger);
                        if (effectiveHandshakeToken is DeliveryToken
                            || effectiveHandshakeToken is StartToken
                                && SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
                        {
                            // Delivery tokens and implicit recovery tokens identify already processed events.
                            consumerData.Cursor = GetAdvancedCursorOrFallback(
                                consumerData,
                                requestedToken,
                                cacheToken,
                                out consumerData.PendingBatch);
                        }
                        else
                        {
                            var result = queueCache.TryGetCacheCursor(consumerData.StreamId, requestedToken);
                            if (result.Kind == QueueCacheCursorResultKind.CacheMiss)
                            {
                                RecordDeliveryProgressError(consumerData, effectiveHandshakeToken);
                            }

                            consumerData.Cursor = result.Kind switch
                            {
                                QueueCacheCursorResultKind.Success => result.Cursor!,
                                QueueCacheCursorResultKind.CacheMiss when cacheToken is not null
                                    => GetCacheCursorOrThrow(consumerData.StreamId, cacheToken),
                                QueueCacheCursorResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
                                _ => throw new InvalidOperationException($"Unexpected cursor result: {result.Kind}."),
                            };
                            if (result.Kind == QueueCacheCursorResultKind.Success)
                            {
                                TrackDeliveryRecoveryCursor(consumerData, consumerData.Cursor, effectiveHandshakeToken);
                            }
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
                            consumerData.Cursor = GetCacheCursorOrThrow(consumerData.StreamId, registrationToken);
                    }
                }
                catch (Exception exception)
                {
                    if (consumerData.IsRemoved) return false;
                    RecordDeliveryProgressError(consumerData, effectiveHandshakeToken);
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
                        forceFaultSubscription
                            || effectiveHandshakeToken is StartPositionToken
                                && exceptionOccured is NotSupportedException);
                    var providerFallbackAllowed = providerDefaultRequest
                        && SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid)
                        && exceptionOccured is NotSupportedException;
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
                    consumerData.Cursor = GetCacheCursorOrThrow(consumerData.StreamId, registrationToken);
                }
                catch (Exception)
                {
                    RecordDeliveryProgressError(consumerData);
                    consumerData.Cursor = GetCacheCursorOrThrow(consumerData.StreamId, null);
                }
            }
            return true;
        }

        private bool ShouldApplyInitialSubscriptionStartPosition(StreamConsumerData consumerData)
            => options.InitialSubscriptionStartPosition != StreamSubscriptionStartPosition.Latest
                && consumerData.LastProcessedToken is null
                && (!consumerData.IsRegistered
                    || consumerData.LastToken is StartPositionToken startPositionToken
                        && startPositionToken.StartPosition == options.InitialSubscriptionStartPosition);

        private static QueueCacheCursorMoveResult AdvanceCursorPastToken(
            IQueueCacheCursor cursor,
            StreamSequenceToken token,
            out IBatchContainer? pendingBatch)
        {
            pendingBatch = null;
            while (true)
            {
                var result = cursor.MoveNextWithResult();
                if (result.Kind is QueueCacheCursorMoveResultKind.NoData or QueueCacheCursorMoveResultKind.CacheMiss)
                {
                    return result;
                }

                if (result.Kind != QueueCacheCursorMoveResultKind.Success)
                {
                    throw new QueueCacheCursorContractException("The cursor move result is not initialized.");
                }

                var batch = GetCurrentBatchOrThrow(cursor);

                var comparison = EventSequenceTokenCompatibility.Compare(batch.SequenceToken, token);
                if (comparison >= 0)
                {
                    pendingBatch = comparison > 0 ? batch : null;
                    return result;
                }
            }
        }

        private IQueueCacheCursor GetCacheCursorAtStartPosition(
            QualifiedStreamId streamId,
            StartPositionToken startPositionToken,
            StreamSequenceToken? latestBoundary)
        {
            if (startPositionToken.StartPosition == StreamSubscriptionStartPosition.Latest
                && latestBoundary is not null)
            {
                return GetCacheCursorOrThrow(streamId, latestBoundary);
            }

            var result = queueCache!.TryGetCacheCursorAtPosition(streamId, startPositionToken.StartPosition);
            return result.Kind switch
            {
                QueueCacheCursorResultKind.Success => result.Cursor!,
                QueueCacheCursorResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
                QueueCacheCursorResultKind.NotSupported => throw new NotSupportedException(
                    $"{queueCache.GetType().FullName} does not support {startPositionToken.StartPosition} cursor positioning."),
                _ => throw new InvalidOperationException("The cursor result is not initialized."),
            };
        }

        private IQueueCacheCursor GetRecoveryCursor(StreamConsumerData consumerData)
        {
            if (consumerData.HasDeliveryProgressError)
            {
                if (consumerData.DeliveryRecoveryToken is { Token: { } requiredToken } recoveryToken)
                {
                    var acquisition = queueCache!.TryGetCacheCursor(consumerData.StreamId, requiredToken);
                    if (acquisition.Kind == QueueCacheCursorResultKind.Success)
                    {
                        var recoveryCursor = acquisition.Cursor!;
                        if (recoveryToken is StartToken
                            || TryAdvanceRecoveryCursor(recoveryCursor, requiredToken, out consumerData.PendingBatch))
                        {
                            TrackDeliveryRecoveryCursor(consumerData, recoveryCursor, recoveryToken);
                            return recoveryCursor;
                        }
                    }
                    else if (acquisition.Kind != QueueCacheCursorResultKind.CacheMiss)
                    {
                        throw new InvalidOperationException($"Unexpected cursor result: {acquisition.Kind}.");
                    }
                }

                return GetCacheCursorOrThrow(consumerData.StreamId, null);
            }

            if (consumerData.LastProcessedToken is { } lastProcessedToken)
            {
                var result = queueCache!.TryGetCacheCursor(consumerData.StreamId, lastProcessedToken);
                if (result.Kind == QueueCacheCursorResultKind.CacheMiss)
                {
                    return GetCacheCursorOrThrow(consumerData.StreamId, null);
                }

                if (result.Kind != QueueCacheCursorResultKind.Success)
                {
                    throw new InvalidOperationException($"Unexpected cursor result: {result.Kind}.");
                }

                var cursor = result.Cursor!;
                if (!TryAdvanceRecoveryCursor(cursor, lastProcessedToken, out consumerData.PendingBatch))
                {
                    return GetCacheCursorOrThrow(consumerData.StreamId, null);
                }

                return cursor;
            }

            if (consumerData.LastToken is StartPositionToken startPositionToken)
            {
                return GetCacheCursorAtStartPosition(consumerData.StreamId, startPositionToken, latestBoundary: null);
            }

            if (consumerData.LastToken is StartToken or DeliveryToken
                && consumerData.LastToken.Token is { } handshakeSequenceToken)
            {
                var result = queueCache!.TryGetCacheCursor(consumerData.StreamId, handshakeSequenceToken);
                if (result.Kind == QueueCacheCursorResultKind.CacheMiss)
                {
                    return GetCacheCursorOrThrow(consumerData.StreamId, null);
                }

                if (result.Kind != QueueCacheCursorResultKind.Success)
                {
                    throw new InvalidOperationException($"Unexpected cursor result: {result.Kind}.");
                }

                var cursor = result.Cursor!;
                if (consumerData.LastToken is DeliveryToken
                    || SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
                {
                    if (!TryAdvanceRecoveryCursor(cursor, handshakeSequenceToken, out consumerData.PendingBatch))
                    {
                        return GetCacheCursorOrThrow(consumerData.StreamId, null);
                    }
                }

                return cursor;
            }

            return GetCacheCursorOrThrow(consumerData.StreamId, null);
        }

        private IQueueCacheCursor GetCacheCursorOrThrow(StreamId streamId, StreamSequenceToken? token)
        {
            var result = queueCache!.TryGetCacheCursor(streamId, token);
            return result.Kind switch
            {
                QueueCacheCursorResultKind.Success => result.Cursor!,
                QueueCacheCursorResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
                _ => throw new InvalidOperationException($"Unexpected cursor result: {result.Kind}."),
            };
        }

        private IQueueCacheCursor GetAdvancedCursorOrFallback(
            StreamConsumerData consumerData,
            StreamSequenceToken token,
            StreamSequenceToken? fallbackToken,
            out IBatchContainer? pendingBatch)
        {
            pendingBatch = null;
            var streamId = consumerData.StreamId;
            var acquisitionResult = queueCache!.TryGetCacheCursor(streamId, token);
            if (acquisitionResult.Kind == QueueCacheCursorResultKind.CacheMiss)
            {
                RecordDeliveryProgressError(consumerData, StreamHandshakeToken.CreateDeliveyToken(token));
                return fallbackToken is not null
                    ? GetCacheCursorOrThrow(streamId, fallbackToken)
                    : throw acquisitionResult.CacheMiss!.Value.ToException();
            }

            if (acquisitionResult.Kind != QueueCacheCursorResultKind.Success)
            {
                throw new InvalidOperationException($"Unexpected cursor result: {acquisitionResult.Kind}.");
            }

            var cursor = acquisitionResult.Cursor!;
            var retainCursor = false;
            try
            {
                var moveResult = AdvanceCursorPastToken(cursor, token, out pendingBatch);
                if (moveResult.Kind == QueueCacheCursorMoveResultKind.CacheMiss)
                {
                    RecordDeliveryProgressError(consumerData, StreamHandshakeToken.CreateDeliveyToken(token));
                    return fallbackToken is not null
                        ? GetCacheCursorOrThrow(streamId, fallbackToken)
                        : throw moveResult.CacheMiss!.Value.ToException();
                }

                if (moveResult.Kind is not QueueCacheCursorMoveResultKind.Success and not QueueCacheCursorMoveResultKind.NoData)
                {
                    throw new InvalidOperationException("The cursor move result is not initialized.");
                }

                retainCursor = true;
                TrackDeliveryRecoveryCursor(consumerData, cursor, StreamHandshakeToken.CreateDeliveyToken(token));
                return cursor;
            }
            finally
            {
                if (!retainCursor)
                {
                    cursor.Dispose();
                }
            }
        }

        private static bool TryAdvanceRecoveryCursor(
            IQueueCacheCursor cursor,
            StreamSequenceToken token,
            out IBatchContainer? pendingBatch)
        {
            var retainCursor = false;
            try
            {
                var result = AdvanceCursorPastToken(cursor, token, out pendingBatch);
                if (result.Kind == QueueCacheCursorMoveResultKind.CacheMiss)
                {
                    return false;
                }

                if (result.Kind is not QueueCacheCursorMoveResultKind.Success and not QueueCacheCursorMoveResultKind.NoData)
                {
                    throw new InvalidOperationException($"Unexpected cursor move result: {result.Kind}.");
                }

                retainCursor = true;
                return true;
            }
            finally
            {
                if (!retainCursor)
                {
                    cursor.Dispose();
                }
            }
        }

        private IQueueCacheCursor GetCacheMissRecoveryCursor(StreamConsumerData consumerData)
        {
            var result = queueCache!.TryGetCacheCursorAtPosition(
                consumerData.StreamId,
                StreamSubscriptionStartPosition.EarliestAvailable);
            return result.Kind switch
            {
                QueueCacheCursorResultKind.Success => result.Cursor!,
                QueueCacheCursorResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
                QueueCacheCursorResultKind.NotSupported => GetRecoveryCursor(consumerData),
                _ => throw new InvalidOperationException("The cursor result is not initialized."),
            };
        }

        public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveSubscriber_Impl(subscriptionId, streamId);
            return Task.CompletedTask;
        }

        public void RemoveSubscriber_Impl(GuidId subscriptionId, QualifiedStreamId streamId)
        {
            if (IsShutdown) return;

            if (!pubSubCache.TryGetValue(streamId, out var streamData)) return;

            if (streamData.TryGetConsumer(subscriptionId, out var consumer))
            {
                pendingConsumerRecoveries.Remove(consumer);
            }

            // remove consumer
            bool removed = streamData.RemoveConsumer(subscriptionId, logger);
            if (removed)
            {
                StreamingEvents.EmitSubscriptionDetached(streamProviderName, streamId.StreamId, subscriptionId.Guid, Silo);
                StreamingEvents.EmitSubscriptionRemoved(streamProviderName, streamId.StreamId, subscriptionId.Guid, Silo);
                LogDebugRemovedConsumer(subscriptionId, streamId);
            }

            if (streamData.Count == 0 && streamData.RegistrationTask is null)
            {
                pubSubCache.Remove(streamId);
                streamData.DisposeAll(logger);
            }
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

                RetryPendingWork(cancellationToken);

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

            RetryPendingWork(cancellationToken);

            // Pause all queue reads so a cold stream's first batch stays pinned until registration completes.
            if (pubSubCache.Values.Any(static stream => stream.RegistrationTask is not null))
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

                foreach (var group in
                    multiBatch
                    .Where(m => m is not null)
                    .GroupBy(container => container.StreamId))
                {
                    var streamId = new QualifiedStreamId(queueAdapter.Name, group.Key);
                    StreamSequenceToken startToken = group.First().SequenceToken;
                    if (pubSubCache.TryGetValue(streamId, out var streamData))
                    {
                        streamData.RefreshActivity(now);
                        StartInactiveCursors(streamData, startToken, CancellationToken.None);
                    }
                    else
                    {
                        // Run registration in the background so that cold-stream pubsub
                        // calls do not stall message delivery for other streams on the same queue.
                        RegisterStream(streamId, startToken, now, CancellationToken.None);
                    }
                }

                accountedFor = true;
            }
            finally
            {
                // The receiver has consumed this read even if cache insertion or registration fails.
                if (!accountedFor)
                {
                    _hasUnknownReplayPosition = true;
                    LogErrorQueueRecoveryRequired(new(QueueId), "A fetched read could not be fully admitted to the cache and stream registry.");
                }
            }

            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            // Only publish the read boundary after every stream has been accounted for.
            foreach (var batch in multiBatch)
            {
                if (batch is null)
                {
                    continue;
                }

                if (batch.SequenceToken is not { } token)
                {
                    _hasUnknownReplayPosition = true;
                    LogErrorQueueRecoveryRequired(new(myQueueId), $"A fetched batch for stream {batch.StreamId} has no sequence token.");
                    continue;
                }

                if (_lastReadToken is null)
                {
                    _lastReadToken = token;
                }
                else if (!TryCompareQueueProgress(token, _lastReadToken, out var comparison))
                {
                    _hasUnknownReplayPosition = true;
                    LogErrorQueueRecoveryRequired(new(myQueueId), "Fetched batches have incompatible sequence tokens.");
                }
                else if (comparison > 0)
                {
                    _lastReadToken = token;
                }
            }

            return true;
        }

        private void CleanupPubSubCache(DateTime now)
        {
            List<QualifiedStreamId>? inactiveStreams = null;
            foreach (var tuple in pubSubCache)
            {
                if (tuple.Value.IsInactive(now, options.StreamInactivityPeriod)
                    && tuple.Value.RegistrationTask is null
                    && !tuple.Value.AllConsumers().Any(consumer => consumer.PendingHandshakes != 0
                        || pendingConsumerRecoveries.Contains(consumer)))
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
                    RetainStreamDeliveryProgress(streamData);
                    streamData.DisposeAll(logger);
                    StreamingEvents.EmitStreamInactive(streamProviderName, streamId.StreamId, options.StreamInactivityPeriod, Silo);
                }
            }
        }

        /// <summary>
        /// Computes delivery progress before shutdown so the queue can persist the latest handoff checkpoint.
        /// </summary>
        private void NotifyDeliveryProgress()
        {
            if (queueCache is null) return;

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            if (TryGetDeliveryProgress(out var earliest))
            {
                queueCache.UpdateDeliveryProgress(earliest, utcNow);
            }
        }

        private bool TryGetDeliveryProgress(out StreamSequenceToken? earliest)
        {
            earliest = _lastReadToken;
            if (_hasUnknownReplayPosition
                || _retainedDeliveryProgress is not null && !IncludeProgress(_retainedDeliveryProgress, ref earliest))
            {
                return false;
            }

            foreach (var streamConsumers in pubSubCache.Values)
            {
                if (!TryGetStreamDeliveryProgress(streamConsumers, out var current)
                    || current is not null && !IncludeProgress(current, ref earliest))
                {
                    return false;
                }
            }

            return true;
        }

        private void RetainStreamDeliveryProgress(StreamConsumerCollection streamData)
        {
            // Reclaim inactive subscription records without forgetting their unresolved handoff constraint.
            if (!TryGetStreamDeliveryProgress(streamData, out var token)
                || token is not null && !IncludeProgress(token, ref _retainedDeliveryProgress))
            {
                _hasUnknownReplayPosition = true;
            }
        }

        private bool TryGetStreamDeliveryProgress(StreamConsumerCollection streamConsumers, out StreamSequenceToken? earliest)
        {
            earliest = null;
            if (!streamConsumers.StreamRegistered
                || streamConsumers.RegistrationTask is not null)
            {
                return false;
            }

            foreach (var consumer in streamConsumers.AllConsumers())
            {
                if (!consumer.IsRegistered || consumer.PendingHandshakes != 0)
                {
                    return false;
                }

                // A drained subscription's last matching event need not pin other streams.
                if (consumer.IsCaughtUp && !consumer.HasDeliveryProgressError)
                {
                    continue;
                }

                var current = consumer.LastProcessedToken;
                if (current is null)
                {
                    return false;
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

            return true;
        }

        private bool IncludeProgress(StreamSequenceToken token, ref StreamSequenceToken? earliest)
        {
            if (earliest is null)
            {
                earliest = token;
            }
            else if (!TryCompareQueueProgress(token, earliest, out var comparison))
            {
                return false;
            }
            else if (comparison < 0)
            {
                earliest = token;
            }

            return true;
        }

        private bool TryCompareQueueProgress(StreamSequenceToken token, StreamSequenceToken other, out int comparison)
        {
            try
            {
                comparison = EventSequenceTokenCompatibility.Compare(token, other);
                return true;
            }
            catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "other")
            {
                LogWarningIncompatibleQueueProgress(new(QueueId), token, other, exception);
                comparison = default;
                return false;
            }
        }

        private void RecordDeliveryProgress(StreamConsumerData consumer, StreamSequenceToken? token)
        {
            // A reset cursor's later progress cannot prove that it did not skip earlier data.
            // An earlier or unknown replay request must still lower the retained checkpoint floor.
            if (!consumer.HasDeliveryProgressError || token is null)
            {
                consumer.LastProcessedToken = token;
                if (token is not null)
                {
                    consumer.PendingStartToken = null;
                }
            }
            else if (consumer.LastProcessedToken is { } current)
            {
                if (!TryCompareQueueProgress(token, current, out var comparison))
                {
                    consumer.LastProcessedToken = null;
                }
                else if (comparison < 0)
                {
                    consumer.LastProcessedToken = token;
                }
            }
        }

        private void RecordReplayRequest(StreamConsumerData consumer, StreamHandshakeToken request)
        {
            if (consumer.HasDeliveryProgressError)
            {
                IncludeDeliveryRecoveryRequest(consumer, NormalizeRecoveryToken(consumer, request));
                consumer.DeliveryRecoveryCursor = null;
                consumer.HasObservedRecoveryStart = false;
            }

            if (request is StartToken)
            {
                consumer.PendingStartToken = request.Token;
            }
            else if (request is StartPositionToken)
            {
                consumer.PendingStartToken = null;
            }
        }

        private static StreamHandshakeToken? NormalizeRecoveryToken(StreamConsumerData consumer, StreamHandshakeToken? token)
            => token is StartToken && SubscriptionMarker.IsImplicitSubscription(consumer.SubscriptionId.Guid)
                ? StreamHandshakeToken.CreateDeliveyToken(token.Token!)
                : token is StartToken or DeliveryToken ? token : null;

        private void IncludeDeliveryRecoveryRequest(StreamConsumerData consumer, StreamHandshakeToken? request)
        {
            if (consumer.DeliveryRecoveryToken?.Token is not { } current
                || request?.Token is not { } requested
                || !TryCompareQueueProgress(requested, current, out var comparison))
            {
                consumer.DeliveryRecoveryToken = null;
                return;
            }

            if (comparison < 0 || comparison == 0 && request is StartToken)
            {
                consumer.DeliveryRecoveryToken = request;
            }
        }

        private void TrackDeliveryRecoveryCursor(
            StreamConsumerData consumer,
            IQueueCacheCursor cursor,
            StreamHandshakeToken? request)
        {
            if (consumer.HasDeliveryProgressError && consumer.DeliveryRecovery is not { Stopped: true }
                && consumer.DeliveryRecoveryToken is { Token: { } required } recovery
                && request?.Token is { } requested
                && TryCompareQueueProgress(requested, required, out var comparison)
                && (comparison < 0 || comparison == 0 && (request is StartToken || recovery is DeliveryToken)))
            {
                consumer.DeliveryRecoveryCursor = cursor;
                consumer.HasObservedRecoveryStart = false;
            }
        }

        private void ObserveDeliveryRecoveryStart(StreamConsumerData consumer, IBatchContainer batch)
        {
            if (!consumer.HasDeliveryProgressError)
            {
                if (consumer.LastProcessedToken is null && batch.SequenceToken is { } first
                    && (consumer.PendingStartToken is null
                        || TryCompareQueueProgress(first, consumer.PendingStartToken, out var startComparison) && startComparison < 0))
                {
                    consumer.PendingStartToken = first;
                }
            }
            else if (ReferenceEquals(consumer.DeliveryRecoveryCursor, consumer.Cursor)
                && consumer.DeliveryRecoveryToken is StartToken { Token: { } required }
                && batch.SequenceToken is { } current
                && TryCompareQueueProgress(current, required, out var anchorComparison) && anchorComparison == 0)
            {
                // Pooled caches can accept a purged token as an exclusive boundary. Inclusive recovery must see it.
                consumer.HasObservedRecoveryStart = true;
            }
        }

        private void RecordAcknowledgedDeliveryProgress(
            StreamConsumerData consumer,
            StreamSequenceToken? token,
            IQueueCacheCursor deliveryCursor,
            long cursorVersion)
        {
            if (consumer.CursorVersion != cursorVersion || !ReferenceEquals(consumer.Cursor, deliveryCursor))
            {
                return;
            }

            // Opening or draining an empty recovery cursor does not prove that its missing data was replayed.
            if (consumer.HasDeliveryProgressError
                && ReferenceEquals(consumer.DeliveryRecoveryCursor, deliveryCursor)
                && consumer.DeliveryRecoveryToken is { Token: { } required } recovery
                && token is not null
                && TryCompareQueueProgress(token, required, out var comparison)
                && (recovery is DeliveryToken && comparison > 0
                    || recovery is StartToken && comparison >= 0 && consumer.HasObservedRecoveryStart))
            {
                consumer.HasDeliveryProgressError = false;
                consumer.DeliveryRecoveryToken = null;
                consumer.DeliveryRecoveryCursor = null;
                consumer.HasObservedRecoveryStart = false;
                consumer.DeliveryRecovery = null;
                consumer.DeliveryRecoveryFailureReported = false;
                consumer.DeliveryRecoveryFailureToken = null;
                pendingConsumerRecoveries.Remove(consumer);
            }

            RecordDeliveryProgress(consumer, token);
            if (token is not null
                && consumer.UnconfirmedDeliveryToken is { } attempted
                && !IsBefore(token, attempted))
            {
                consumer.UnconfirmedDeliveryToken = null;
            }
        }

        private void RecordDeliveryProgressError(
            StreamConsumerData consumer,
            StreamHandshakeToken? replayRequest = null,
            StreamSequenceToken? failedToken = null,
            long? startedAt = null)
        {
            if (consumer.IsRemoved) return;
            var request = NormalizeRecoveryToken(consumer, replayRequest);
            if (!consumer.HasDeliveryProgressError)
            {
                var first = consumer.PendingStartToken;
                if (failedToken is not null)
                {
                    if (first is null)
                    {
                        first = failedToken;
                    }
                    else if (!TryCompareQueueProgress(failedToken, first, out var comparison))
                    {
                        first = null;
                    }
                    else if (comparison < 0)
                    {
                        first = failedToken;
                    }
                }

                consumer.DeliveryRecoveryToken = replayRequest is not null
                    ? request
                    : consumer.LastProcessedToken is { } processed
                        ? StreamHandshakeToken.CreateDeliveyToken(processed)
                        : first is { } start
                            ? StreamHandshakeToken.CreateStartToken(start)
                            : null;
            }
            else if (replayRequest is not null)
            {
                IncludeDeliveryRecoveryRequest(consumer, request);
            }

            if (!consumer.HasDeliveryProgressError)
            {
                consumer.DeliveryRecovery = new(startedAt ?? _timeProvider.GetTimestamp());
                LogWarningDeliveryRecoveryRequired(new(QueueId), consumer.SubscriptionId, consumer.StreamId);
            }

            consumer.HasDeliveryProgressError = true;
            consumer.IsCaughtUp = false;
            consumer.DeliveryRecoveryCursor = null;
            consumer.HasObservedRecoveryStart = false;
            if (consumer.DeliveryRecovery is not { Stopped: true })
            {
                pendingConsumerRecoveries.Add(consumer);
            }
        }

        private void RetryPendingConsumers()
        {
            foreach (var consumer in pendingConsumerRecoveries.ToArray())
            {
                if (!pubSubCache.TryGetValue(consumer.StreamId, out var stream)
                    || !stream.TryGetConsumer(consumer.SubscriptionId, out var current)
                    || !ReferenceEquals(current, consumer)
                    || !consumer.HasDeliveryProgressError
                    || consumer.DeliveryRecovery is { Stopped: true })
                {
                    pendingConsumerRecoveries.Remove(consumer);
                }
                else if (consumer.IsRegistered && consumer.PendingHandshakes == 0
                    && consumer.State == StreamConsumerDataState.Inactive)
                {
                    RunConsumerCursor(consumer).Ignore();
                }
            }
        }

        private void RetryPendingWork(CancellationToken cancellationToken)
        {
            RetryPendingConsumers();
            foreach (var pending in pubSubCache
                .Where(static pair => pair.Value.RegistrationTask is { IsCompleted: true })
                .ToArray())
            {
                RegisterStream(pending.Key, pending.Value.RegistrationStartToken,
                    _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            }
        }

        private async Task StopConsumerRecovery(StreamConsumerData consumer, CancellationToken cancellationToken)
        {
            if (consumer.DeliveryRecovery is not { Stopped: false } recovery)
            {
                return;
            }

            recovery.Stopped = true;
            pendingConsumerRecoveries.Remove(consumer);
            consumer.SafeDisposeCursor(logger);
            consumer.PendingStartToken = null;
            consumer.PendingContinuationToken = null;
            consumer.UnconfirmedDeliveryToken = null;
            LogWarningDeliveryRecoveryStopped(new(QueueId), consumer.SubscriptionId, consumer.StreamId, recovery.Attempts);
            if (!consumer.DeliveryRecoveryFailureReported)
            {
                // A replay boundary is not necessarily a failed event (failure handlers can dead-letter this token).
                await ErrorProtocol(consumer, new StreamEventDeliveryFailureException(consumer.StreamId),
                    true, null, null, cancellationToken: cancellationToken);
            }
        }

        private static bool IsBefore(StreamSequenceToken current, StreamSequenceToken other)
            => EventSequenceTokenCompatibility.Compare(current, other) < 0;

        private void RegisterStream(
            QualifiedStreamId streamId,
            StreamSequenceToken? firstToken,
            DateTime now,
            CancellationToken cancellationToken)
        {
            if (IsShutdown || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!pubSubCache.TryGetValue(streamId, out var streamData))
            {
                streamData = new StreamConsumerCollection(now) { RegistrationStartToken = firstToken };
                pubSubCache.Add(streamId, streamData);
            }
            else if (streamData.RegistrationTask is null || !streamData.RegistrationTask.IsCompleted)
            {
                return;
            }

            var recovery = streamData.RegistrationRecovery ??= new(_timeProvider.GetTimestamp());
            if (recovery.IsExhausted(_timeProvider, options.MaxEventDeliveryTime, RecoveryAttemptMax))
            {
                recovery.Stopped = true;
                RetainStreamDeliveryProgress(streamData);
                foreach (var consumer in streamData.AllConsumers())
                {
                    pendingConsumerRecoveries.Remove(consumer);
                }
                pubSubCache.Remove(streamId);
                streamData.DisposeAll(logger);
                LogWarningStreamRegistrationRecoveryStopped(new(QueueId), streamId, recovery.Attempts);
                return;
            }

            if (!recovery.TryBeginAttempt(_timeProvider)) return;
            streamData.RegistrationTask = RegisterStreamAsync();

            async Task RegisterStreamAsync()
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

                var completed = false;
                try
                {
                    if (IsShutdown || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Retain the original boundary and pin across retries, including failures before producer registration.
                    if (queueCache is not null)
                    {
                        streamData.RegistrationCursor ??= GetCacheCursorOrThrow(streamId, streamData.RegistrationStartToken);
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

                    completed = true;
                    if (subscribers.Count > 0)
                    {
                        var addSubscriptionTasks = new List<Task<bool>>(subscribers.Count);
                        foreach (PubSubSubscriptionState item in subscribers)
                        {
                            addSubscriptionTasks.Add(SubscribeWithIsolation(item));
                        }

                        completed = (await Task.WhenAll(addSubscriptionTasks)).All(static attached => attached);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completed = false;
                    // The retained stream entry remains pending for the next uncanceled pump.
                }
                catch (Exception exception)
                {
                    completed = false;
                    LogWarningFailedToRegisterStream(streamId, exception);
                    StreamingEvents.EmitPullingAgentStreamRegistrationFailed(streamProviderName, Silo, QueueId, streamId.StreamId, exception);
                }
                finally
                {
                    if (completed)
                    {
                        streamData.RegistrationTask = null;
                        streamData.RegistrationStartToken = null;
                        streamData.RegistrationRecovery = null;
                        streamData.DisposeRegistrationCursor(logger);
                    }
                    else
                    {
                        streamData.RegistrationTask = Task.CompletedTask;
                        recovery.FinishAttempt(_timeProvider, deliveryBackoffProvider);
                        if (IsShutdown
                            || !pubSubCache.TryGetValue(streamId, out var current) || !ReferenceEquals(current, streamData))
                        {
                            streamData.DisposeRegistrationCursor(logger);
                        }
                    }
                }
            }

            async Task<bool> SubscribeWithIsolation(PubSubSubscriptionState item)
            {
                if (IsShutdown)
                {
                    return false;
                }

                if (streamData.TryGetConsumer(item.SubscriptionId, out var existing) && existing.IsRegistered)
                {
                    return existing.PendingHandshakes == 0;
                }

                try
                {
                    await AddSubscriber_Impl(item.SubscriptionId, item.Stream, item.Consumer, item.FilterData, streamData.RegistrationStartToken);
                    return !streamData.TryGetConsumer(item.SubscriptionId, out var consumer) || consumer.IsRegistered;
                }
                catch (Exception exception)
                {
                    LogWarningFailedToAddSubscription(item.Stream, exception);
                    return false;
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
                consumerData.IsCaughtUp = false;
                if (consumerData.IsRegistered)
                {
                    if (consumerData.DeliveryRecovery is { Stopped: true } && consumerData.Cursor is null)
                    {
                        // A nonfaulting subscription still receives new traffic, but does not restart abandoned replay.
                        consumerData.PendingContinuationToken ??= startToken;
                    }

                    // Preserve recovery positions so the next move can detect missing replay data.
                    if (consumerData.Cursor is { } cursor && !ReferenceEquals(cursor, consumerData.DeliveryRecoveryCursor))
                    {
                        cursor.Refresh(startToken);
                    }

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

        private async Task RunConsumerCursor(StreamConsumerData consumerData, CancellationToken cancellationToken = default, bool useCurrentCursor = false)
        {
            TagList? tags = null;
            StreamRecoveryState? recoveryAttempt = null;
            var ownsCursor = false;
            try
            {
                // double check in case of interleaving
                if (IsShutdown || cancellationToken.IsCancellationRequested
                    || consumerData.IsRemoved || consumerData.State == StreamConsumerDataState.Active
                    || consumerData.PendingHandshakes != 0) return;

                if (consumerData.HasDeliveryProgressError && consumerData.PendingHandshakes == 0
                    && consumerData.DeliveryRecovery is { Stopped: false } recovery)
                {
                    if (consumerData.DeliveryRecoveryToken is null && !useCurrentCursor
                        || recovery.IsExhausted(_timeProvider, options.MaxEventDeliveryTime, RecoveryAttemptMax))
                    {
                        consumerData.State = StreamConsumerDataState.Active;
                        ownsCursor = true;
                        await StopConsumerRecovery(consumerData, cancellationToken);
                        return;
                    }

                    if (!recovery.TryBeginAttempt(_timeProvider)) return;
                    recoveryAttempt = recovery;
                }

                var needsRecovery = consumerData.HasDeliveryProgressError && consumerData.DeliveryRecoveryToken is not null
                    && consumerData.DeliveryRecovery is not { Stopped: true }
                    && !useCurrentCursor
                    && consumerData.PendingHandshakes == 0
                    && (consumerData.Cursor is null || !ReferenceEquals(consumerData.Cursor, consumerData.DeliveryRecoveryCursor));
                var continueNewTraffic = consumerData.DeliveryRecovery is { Stopped: true }
                    && consumerData.Cursor is null && consumerData.PendingContinuationToken is not null;
                if (consumerData.Cursor is null && !needsRecovery && !continueNewTraffic) return;

                consumerData.State = StreamConsumerDataState.Active;
                ownsCursor = true;
                var replacedCursor = needsRecovery || continueNewTraffic;
                if (needsRecovery)
                {
                    consumerData.SafeDisposeCursor(logger);
                    consumerData.Cursor = GetRecoveryCursor(consumerData);
                }
                else if (continueNewTraffic)
                {
                    var token = consumerData.PendingContinuationToken;
                    consumerData.PendingContinuationToken = null;
                    consumerData.Cursor = GetCacheCursorOrThrow(consumerData.StreamId, token);
                }

                var deliveredAny = false;
                while (!IsShutdown && !cancellationToken.IsCancellationRequested && consumerData.Cursor is not null)
                {
                    if (consumerData.PendingHandshakes != 0) break;
                    var deliveryCursor = consumerData.Cursor;
                    var cursorVersion = consumerData.CursorVersion;
                    var batchCursor = options.BatchContainerBatchSize > 1
                        ? consumerData.Cursor as IQueueCacheCursorBatchDelivery
                        : null;
                    using var deliveryBatch = batchCursor?.ProtectDeliveryBatch();
                    ConsumerBatch nextBatch = default;
                    Exception? exceptionOccured = null;
                    var forceFaultSubscription = false;
                    var deliveryPending = false;
                    try
                    {
                        nextBatch = GetBatchForConsumer(consumerData);
                        if (nextBatch.CursorResult.Kind == QueueCacheCursorMoveResultKind.CacheMiss)
                        {
                            RecordDeliveryProgressError(consumerData);
                            consumerData.SafeDisposeCursor(logger);
                            if (replacedCursor || consumerData.DeliveryRecovery is { Stopped: true })
                            {
                                break;
                            }

                            if (recoveryAttempt is null)
                            {
                                recoveryAttempt = consumerData.DeliveryRecovery!;
                                if (!recoveryAttempt.TryBeginAttempt(_timeProvider)) break;
                            }
                            replacedCursor = true;
                            consumerData.Cursor = GetCacheMissRecoveryCursor(consumerData);
                            continue;
                        }

                        if (nextBatch.CursorResult.Kind == QueueCacheCursorMoveResultKind.Invalid)
                        {
                            throw new QueueCacheCursorContractException("The cursor move result is not initialized.");
                        }

                        if (!nextBatch.HasProgress)
                        {
                            consumerData.IsCaughtUp = nextBatch.CursorResult.Kind == QueueCacheCursorMoveResultKind.NoData
                                && !consumerData.HasDeliveryProgressError
                                && consumerData.UnconfirmedDeliveryToken is null;
                            // Only emit cursor-drained when we transitioned from delivering to empty,
                            // not on every empty poll.
                            if (deliveredAny)
                                StreamingEvents.EmitConsumerCursorDrained(streamProviderName, consumerData.StreamId.StreamId, consumerData.SubscriptionId.Guid, Silo);
                            break;
                        }
                    }
                    catch (QueueCacheCursorContractException)
                    {
                        RecordDeliveryProgressError(consumerData);
                        consumerData.SafeDisposeCursor(logger);
                        throw;
                    }
                    catch (Exception exc)
                    {
                        RecordDeliveryProgressError(consumerData);
                        exceptionOccured = exc;
                        consumerData.SafeDisposeCursor(logger);
                        if (recoveryAttempt is null && consumerData.DeliveryRecovery is { Stopped: false } recoveryState
                            && recoveryState.TryBeginAttempt(_timeProvider))
                        {
                            recoveryAttempt = recoveryState;
                            replacedCursor = true;
                            consumerData.Cursor = GetRecoveryCursor(consumerData);
                        }
                    }

                    if (exceptionOccured is null)
                    {
                        deliveredAny = true;

                        if (nextBatch.Batch is null)
                        {
                            RecordAcknowledgedDeliveryProgress(consumerData, nextBatch.ProgressToken, deliveryCursor, cursorVersion);
                            continue;
                        }
                    }

                    long? deliveryStartedAt = null;
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
                            if (consumerData.UnconfirmedDeliveryToken is null
                                || IsBefore(consumerData.UnconfirmedDeliveryToken, nextBatch.ProgressToken!))
                            {
                                consumerData.UnconfirmedDeliveryToken = nextBatch.ProgressToken;
                            }

                            var deliveryTime = options.MaxEventDeliveryTime;
                            if (consumerData.DeliveryRecovery is { Stopped: false } activeRecovery)
                            {
                                deliveryTime = activeRecovery.RemainingTime(_timeProvider, deliveryTime);
                                if (options.MaxEventDeliveryTime > TimeSpan.Zero && deliveryTime <= TimeSpan.Zero)
                                {
                                    await StopConsumerRecovery(consumerData, cancellationToken);
                                    break;
                                }
                            }

                            deliveryPending = true;
                            deliveryStartedAt = _timeProvider.GetTimestamp();
                            StreamHandshakeToken? newToken = await AsyncExecutorWithRetries.ExecuteWithRetries(
                                i => DeliverBatchToConsumer(consumerData, batch, cursorVersion, cancellationToken),
                                consumerData.DeliveryRecovery is { Stopped: false } ? 1 : AsyncExecutorWithRetries.INFINITE_RETRIES,
                                // Do not retry if the agent is shutting down, or if the exception is ClientNotAvailableException
                                (exception, i) => exception is not ClientNotAvailableException && !IsShutdown
                                    && consumerData.CursorVersion == cursorVersion,
                                deliveryTime,
                                deliveryBackoffProvider,
                                cancellationToken: cancellationToken);
                            deliveryPending = false;
                            if (consumerData.CursorVersion != cursorVersion)
                            {
                                continue;
                            }

                            StreamSequenceToken? acknowledgedToken = null;
                            if (newToken is not null)
                            {
                                RecordReplayRequest(consumerData, newToken);
                                consumerData.LastToken = newToken;
                                IQueueCacheCursor newCursor;
                                IBatchContainer? pendingBatch = null;
                                if (newToken is StartPositionToken startPositionToken)
                                {
                                    RecordDeliveryProgress(consumerData, null);
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
                                        acknowledgedToken = sequenceToken;
                                        RecordDeliveryProgress(consumerData, acknowledgedToken);
                                        // An implicit recovery token identifies the last event processed by the prior activation.
                                        // The current batch is the receiver's first available message if that token was evicted.
                                        newCursor = GetAdvancedCursorOrFallback(
                                            consumerData,
                                            sequenceToken,
                                            batch.SequenceToken,
                                            out pendingBatch);
                                    }
                                    else
                                    {
                                        RecordDeliveryProgress(consumerData, null);
                                        newCursor = GetCacheCursorOrThrow(consumerData.StreamId, sequenceToken);
                                        TrackDeliveryRecoveryCursor(consumerData, newCursor, newToken);
                                    }
                                }
                                else if (newToken is DeliveryToken)
                                {
                                    var sequenceToken = newToken.Token
                                        ?? throw new InvalidOperationException("A delivery handshake token must contain a stream sequence token.");
                                    acknowledgedToken = sequenceToken;
                                    RecordDeliveryProgress(consumerData, acknowledgedToken);
                                    // The handshake token points to an already processed event, so advance past it.
                                    // The current batch is the receiver's first available message if that token was evicted.
                                    newCursor = GetAdvancedCursorOrFallback(
                                        consumerData,
                                        sequenceToken,
                                        batch.SequenceToken,
                                        out pendingBatch);
                                }
                                else
                                {
                                    forceFaultSubscription = true;
                                    throw new InvalidOperationException($"Unsupported stream handshake token type {newToken.GetType().FullName}.");
                                }

                                consumerData.SafeDisposeCursor(logger);
                                consumerData.Cursor = newCursor;
                                consumerData.PendingBatch = pendingBatch;
                            }
                            else
                            {
                                acknowledgedToken = nextBatch.ProgressToken;
                                RecordAcknowledgedDeliveryProgress(consumerData, acknowledgedToken, deliveryCursor, cursorVersion);
                            }

                            // A rewind or an explicit start request does not acknowledge the attempted batch.
                            if (acknowledgedToken is { } processed
                                && consumerData.UnconfirmedDeliveryToken is { } attempted
                                && !IsBefore(processed, attempted))
                            {
                                consumerData.UnconfirmedDeliveryToken = null;
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        if (deliveryPending && consumerData.CursorVersion != cursorVersion)
                        {
                            continue;
                        }

                        RecordDeliveryProgressError(consumerData, failedToken: nextBatch.Batch?.SequenceToken,
                            startedAt: deliveryStartedAt);
                        if (batchCursor is not null && nextBatch.Batch is not null)
                        {
                            batchCursor.RecordDeliveryFailure(nextBatch.Batch);
                        }
                        else
                        {
                            consumerData.Cursor?.RecordDeliveryFailure();
                        }

                        LogErrorDeliveringMessages(consumerData.StreamId, exc);

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
                            forceFaultSubscription,
                            cancellationToken);
                        if (faultedSubscription) return;
                        if (nextBatch.Batch is not null)
                        {
                            // Retry through the normal pump rather than skipping the failed delivery or spinning here.
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exc)
            {
                RecordDeliveryProgressError(consumerData);
                // RunConsumerCursor is fired with .Ignore so we should log if anything goes wrong, because there is no one to catch the exception
                LogErrorRunConsumerCursor(exc);
                throw;
            }
            finally
            {
                if (ownsCursor)
                {
                    try
                    {
                        if (recoveryAttempt is not null && ReferenceEquals(recoveryAttempt, consumerData.DeliveryRecovery)
                            && !recoveryAttempt.Stopped && !IsShutdown)
                        {
                            recoveryAttempt.FinishAttempt(_timeProvider, deliveryBackoffProvider);
                            if (recoveryAttempt.IsExhausted(_timeProvider, options.MaxEventDeliveryTime, RecoveryAttemptMax))
                            {
                                await StopConsumerRecovery(consumerData, cancellationToken);
                            }
                        }
                    }
                    finally
                    {
                        consumerData.State = StreamConsumerDataState.Inactive;
                        if (!IsShutdown && !consumerData.IsRemoved && consumerData.PendingHandshakes == 0
                            && consumerData.DeliveryRecovery is { Stopped: true }
                            && consumerData.PendingContinuationToken is not null)
                        {
                            RunConsumerCursor(consumerData).Ignore();
                        }
                    }
                }
            }
        }

        private readonly struct ConsumerBatch
        {
            public ConsumerBatch(IBatchContainer? batch, StreamSequenceToken? progressToken)
            {
                Batch = batch;
                ProgressToken = progressToken;
                CursorResult = QueueCacheCursorMoveResult.Success;
            }

            public ConsumerBatch(QueueCacheCursorMoveResult cursorResult) => CursorResult = cursorResult;

            public IBatchContainer? Batch { get; }
            public StreamSequenceToken? ProgressToken { get; }
            public QueueCacheCursorMoveResult CursorResult { get; }
            public bool HasProgress => ProgressToken is not null;
        }

        private ConsumerBatch GetBatchForConsumer(StreamConsumerData consumerData)
        {
            var cursor = consumerData.Cursor!;
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
                else
                {
                    var result = cursor.MoveNextWithResult();
                    if (result.Kind is QueueCacheCursorMoveResultKind.NoData or QueueCacheCursorMoveResultKind.CacheMiss)
                    {
                        return new ConsumerBatch(result);
                    }

                    if (result.Kind != QueueCacheCursorMoveResultKind.Success)
                    {
                        throw new QueueCacheCursorContractException("The cursor move result is not initialized.");
                    }

                    batchContainer = GetCurrentBatchOrThrow(cursor);
                }

                ObserveDeliveryRecoveryStart(consumerData, batchContainer);
                return ShouldDeliverBatch(streamId, batchContainer, filterData)
                    ? new ConsumerBatch(batchContainer, batchContainer.SequenceToken)
                    : new ConsumerBatch(null, batchContainer.SequenceToken);
            }
            else if (this.options.BatchContainerBatchSize > 1)
            {
                int i = 0;
                var batchContainers = new List<IBatchContainer>();
                StreamSequenceToken? progressToken = null;
                var sawBatch = pendingBatch is not null;

                if (pendingBatch is not null)
                {
                    ObserveDeliveryRecoveryStart(consumerData, pendingBatch);
                    progressToken = pendingBatch.SequenceToken;
                    if (ShouldDeliverBatch(streamId, pendingBatch, filterData))
                    {
                        batchContainers.Add(pendingBatch);
                        i++;
                    }
                }

                while (i < this.options.BatchContainerBatchSize)
                {
                    var result = cursor.MoveNextWithResult();
                    if (result.Kind == QueueCacheCursorMoveResultKind.CacheMiss)
                    {
                        return new ConsumerBatch(result);
                    }

                    if (result.Kind == QueueCacheCursorMoveResultKind.NoData)
                    {
                        break;
                    }

                    if (result.Kind != QueueCacheCursorMoveResultKind.Success)
                    {
                        throw new QueueCacheCursorContractException("The cursor move result is not initialized.");
                    }

                    var batchContainer = GetCurrentBatchOrThrow(cursor);
                    ObserveDeliveryRecoveryStart(consumerData, batchContainer);
                    sawBatch = true;
                    progressToken = batchContainer.SequenceToken;

                    if (!ShouldDeliverBatch(streamId, batchContainer, filterData))
                        continue;

                    batchContainers.Add(batchContainer);
                    i++;
                }

                if (progressToken is null)
                {
                    return new ConsumerBatch(sawBatch ? QueueCacheCursorMoveResult.Success : QueueCacheCursorMoveResult.NoData);
                }

                return i == 0
                    ? new ConsumerBatch(null, progressToken)
                    : new ConsumerBatch(new BatchContainerBatch(batchContainers), progressToken);
            }

            return default;
        }

        private static IBatchContainer GetCurrentBatchOrThrow(IQueueCacheCursor cursor)
        {
            var batch = cursor.GetCurrent(out var exception);
            if (exception is not null)
            {
                throw exception;
            }

            return batch ?? throw new QueueCacheCursorContractException("A successful cursor move did not produce a current item.");
        }

        private sealed class QueueCacheCursorContractException(string message) : InvalidOperationException(message);

        private async Task<StreamHandshakeToken?> DeliverBatchToConsumer(
            StreamConsumerData consumerData,
            IBatchContainer batch,
            long cursorVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                StreamHandshakeToken? newToken = await ContextualizedDeliverBatchToConsumer(consumerData, batch, cancellationToken);
                if (consumerData.CursorVersion == cursorVersion)
                {
                    consumerData.LastToken = StreamHandshakeToken.CreateDeliveyToken(batch.SequenceToken); // this is the currently delivered token
                    consumerData.StartPositionIsProviderDefault = false;
                }
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
            CancellationToken cancellationToken)
        {
            bool isRequestContextSet = batch.ImportRequestContext();
            try
            {
                var handshakeToken = consumerData.StartPositionIsProviderDefault ? null : consumerData.LastToken;
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
            bool forceFaultSubscription = false,
            CancellationToken cancellationToken = default)
        {
            if (consumerData.IsRemoved) return true;
            // for loss of client, we just remove the subscription
            if (exceptionOccured is ClientNotAvailableException)
            {
                LogWarningConsumerIsDead(consumerData.StreamConsumer, consumerData.StreamId);
                pubSub.UnregisterConsumer(consumerData.SubscriptionId, consumerData.StreamId, cancellationToken).Ignore();
                return true;
            }

            if (consumerData.HasDeliveryProgressError)
            {
                if (consumerData.DeliveryRecovery is { Stopped: false }
                    && consumerData.DeliveryRecoveryFailureReported && !forceFaultSubscription
                    && consumerData.DeliveryRecoveryFailureIsDelivery == isDeliveryError
                    && Equals(consumerData.DeliveryRecoveryFailureToken, token))
                {
                    return false;
                }

                consumerData.DeliveryRecoveryFailureReported = true;
                consumerData.DeliveryRecoveryFailureIsDelivery = isDeliveryError;
                consumerData.DeliveryRecoveryFailureToken = token;
            }

            // notify consumer about the error or that the data is not available.
            await DeliverErrorToConsumer(consumerData, exceptionOccured, batch, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (consumerData.IsRemoved) return true;
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

            if (consumerData.IsRemoved) return true;
            // if configured to fault on delivery failure and this is not an implicit subscription, fault and remove the subscription
            if ((forceFaultSubscription || streamFailureHandler.ShouldFaultSubsriptionOnError)
                && !SubscriptionMarker.IsImplicitSubscription(consumerData.SubscriptionId.Guid))
            {
                try
                {
                    // notify consumer of faulted subscription, if we can.
                    await DeliverErrorToConsumer(
                        consumerData,
                        new FaultedSubscriptionException(consumerData.SubscriptionId, consumerData.StreamId),
                        batch,
                        cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

                    if (consumerData.IsRemoved) return true;
                    // mark subscription as faulted.
                    await pubSub.FaultSubscription(consumerData.StreamId, consumerData.SubscriptionId, cancellationToken);
                }
                finally
                {
                    // remove subscription
                    if (!consumerData.IsRemoved)
                    {
                        RemoveSubscriber_Impl(consumerData.SubscriptionId, consumerData.StreamId);
                    }
                }
                return true;
            }
            return false;
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
            Message = "Retaining the previous delivery watermark for queue {Queue}: token {Token} is incompatible with {OtherToken}."
        )]
        private partial void LogWarningIncompatibleQueueProgress(
            QueueIdLogRecord queue,
            StreamSequenceToken token,
            StreamSequenceToken otherToken,
            Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Cannot establish delivery checkpoint progress for queue {Queue}. {Reason} Receiver/provider recovery is required; later reads in this receiver lifetime do not establish replay continuity."
        )]
        private partial void LogErrorQueueRecoveryRequired(QueueIdLogRecord queue, string reason);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Delivery progress is constrained for subscription {SubscriptionId} on stream {StreamId} in queue {Queue}. Continuous replay is required to resume progress; unavailable data requires receiver/provider recovery or an explicit subscription resolution."
        )]
        private partial void LogWarningDeliveryRecoveryRequired(QueueIdLogRecord queue, GuidId subscriptionId, QualifiedStreamId streamId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Stopping automatic replay after {Attempts} attempts for subscription {SubscriptionId} on stream {StreamId} in queue {Queue}. The unresolved delivery checkpoint constraint is retained; nonfaulting subscriptions can continue with new traffic."
        )]
        private partial void LogWarningDeliveryRecoveryStopped(QueueIdLogRecord queue, GuidId subscriptionId, QualifiedStreamId streamId, int attempts);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Stopping automatic registration recovery after {Attempts} attempts for stream {StreamId} in queue {Queue}. Registration resources are released and delivery checkpoint progress remains unknown."
        )]
        private partial void LogWarningStreamRegistrationRecoveryStopped(QueueIdLogRecord queue, QualifiedStreamId streamId, int attempts);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.PersistentStreamPullingAgent_13,
            Message = "Ignoring exception while trying to evaluate subscription filter '{Filter}' with data '{FilterData}' on stream {StreamId}"
        )]
        private partial void LogWarningFilterEvaluation(string filter, string? filterData, StreamId streamId, Exception exception);
    }
}
