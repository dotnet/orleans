using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Dissemination;
using Orleans.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// This class collects runtime statistics for all silos in the current deployment for use by placement.
    /// </summary>
    internal sealed partial class DeploymentLoadPublisher : SystemTarget, IDeploymentLoadPublisher, ISiloStatusListener, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILocalSiloDetails _siloDetails;
        private readonly ISiloStatusOracle _siloStatusOracle;
        private readonly IInternalGrainFactory _grainFactory;
        private readonly ActivationDirectory _activationDirectory;
        private readonly IActivationWorkingSet _activationWorkingSet;
        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly IOptions<LoadSheddingOptions> _loadSheddingOptions;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> _periodicStats;
        private readonly TimeSpan _statisticsRefreshTime;
        private readonly List<ISiloStatisticsChangeListener> _siloStatisticsChangeListeners;
        private readonly ILogger _logger;

        private long _lastUpdateDateTimeTicks;
        private IGrainTimer? _publishTimer;
        private Task<StatisticsPublication>? _publicationTask;
        private Task<StatisticsPublication>? _disseminationPublicationTask;

        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics => _periodicStats;

        public SiloRuntimeStatistics LocalRuntimeStatistics { get; private set; } = null!;

        public DeploymentLoadPublisher(
            ILocalSiloDetails siloDetails,
            ISiloStatusOracle siloStatusOracle,
            IOptions<DeploymentLoadPublisherOptions> options,
            IInternalGrainFactory grainFactory,
            ILoggerFactory loggerFactory,
            ActivationDirectory activationDirectory,
            IActivationWorkingSet activationWorkingSet,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            IServiceProvider serviceProvider,
            SystemTargetShared shared)
            : base(Constants.DeploymentLoadPublisherSystemTargetType, shared)
        {
            _logger = loggerFactory.CreateLogger<DeploymentLoadPublisher>();
            _siloDetails = siloDetails;
            _siloStatusOracle = siloStatusOracle;
            _grainFactory = grainFactory;
            _activationDirectory = activationDirectory;
            _activationWorkingSet = activationWorkingSet;
            _environmentStatisticsProvider = environmentStatisticsProvider;
            _loadSheddingOptions = loadSheddingOptions;
            _serviceProvider = serviceProvider;
            _statisticsRefreshTime = options.Value.DeploymentLoadPublisherRefreshTime;
            _periodicStats = new ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>();
            _siloStatisticsChangeListeners = new List<ISiloStatisticsChangeListener>();
            siloStatusOracle.SubscribeToSiloStatusEvents(this);
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        private async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDebugStartingDeploymentLoadPublisher(_logger);

            if (_statisticsRefreshTime > TimeSpan.Zero)
            {
                // Randomize PublishStatistics timer,
                // but also upon start publish my stats to everyone and take everyone's stats for me to start with something.
                var randomTimerOffset = RandomTimeSpan.Next(_statisticsRefreshTime);
                await this.RunOrQueueTask(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _publishTimer = RegisterGrainTimer(PublishStatisticsOnTimer, randomTimerOffset, _statisticsRefreshTime);
                    return Task.CompletedTask;
                });
            }

            await RefreshClusterStatistics(cancellationToken);
            await this.RunOrQueueTask(async token =>
            {
                await PublishStatistics(token);
                return true;
            }, cancellationToken);
            LogDebugStartedDeploymentLoadPublisher(_logger);
        }

        internal Task PublishStatistics(CancellationToken cancellationToken) =>
            PublishStatisticsAndGetReceipt(cancellationToken);

        private async Task PublishStatisticsOnTimer(CancellationToken cancellationToken)
        {
            var publication = await PublishStatisticsAndGetReceipt(cancellationToken);
            if (publication.Receipt.Accepted && !cancellationToken.IsCancellationRequested)
            {
                // Change during a grain timer callback applies its due time after the callback
                // completes. Account for direct delivery and notifications since receipt arrival,
                // rather than adding another full period after the root's cohort wait.
                // Startup and explicit publications must not change a paused/disposed timer.
                var remaining = publication.Receipt.NextPublicationDelay
                    - publication.TimeProvider!.GetElapsedTime(publication.ReceiptTimestamp);
                _publishTimer?.Change(
                    remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                    _statisticsRefreshTime);
            }
        }

        private async Task<StatisticsPublication> PublishStatisticsAndGetReceipt(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Startup and the interleaving timer can overlap. Join the pending sample instead of
            // submitting another source publication while its cohort is still open.
            if (_publicationTask is { IsCompleted: false } pending)
            {
                return await AwaitSharedPublication(pending, cancellationToken);
            }

            // Native operations own this caller's cancellation and may complete successfully
            // after observing it. Only joining callers need an independently cancellable wait.
            return await (_publicationTask = PublishStatisticsCore(cancellationToken));
        }

        private async Task<StatisticsPublication> PublishStatisticsCore(CancellationToken cancellationToken)
        {
            StatisticsPublication publication = default;
            try
            {
                LogTracePublishStatistics(_logger);

                // Ensure that our timestamp is monotonically increasing.
                var ticks = _lastUpdateDateTimeTicks = Math.Max(_lastUpdateDateTimeTicks + 1, DateTime.UtcNow.Ticks);

                var myStats = new SiloRuntimeStatistics(
                    _activationDirectory.Count,
                    _activationWorkingSet.Count,
                    _environmentStatisticsProvider,
                    _loadSheddingOptions,
                    new DateTime(ticks, DateTimeKind.Utc));

                // Update statistics locally.
                LocalRuntimeStatistics = myStats;
                UpdateRuntimeStatisticsInternal(_siloDetails.SiloAddress, myStats);
                DeploymentLoadPublisherEvents.EmitPublished(_siloDetails.SiloAddress, myStats);

                // Inform other cluster members about our refreshed statistics.
                var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToArray();
                IReadOnlyCollection<SiloAddress> directRecipients = members;
                publication = await PublishStatisticsViaDissemination(myStats, cancellationToken);
                if (publication.Receipt.Accepted)
                {
                    try
                    {
                        var dissemination = _serviceProvider.GetRequiredService<IDisseminationService>();
                        var disseminationNamespace = _serviceProvider.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
                        var unconfirmedPeers = dissemination.GetUnconfirmedPeers(disseminationNamespace);
                        if (unconfirmedPeers.Count == 0)
                        {
                            directRecipients = [];
                        }
                        else
                        {
                            // An unsupported intermediate node can separate otherwise capable tree participants.
                            var unconfirmed = unconfirmedPeers.ToHashSet();
                            directRecipients = members.Any(unconfirmed.Contains) ? members : [];
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                    }
                }

                await PublishStatisticsDirectly(myStats, directRecipients, cancellationToken);
                DeploymentLoadPublisherEvents.EmitClusterRefreshed(_siloDetails.SiloAddress, _periodicStats);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exc)
            {
                LogWarningRuntimeStatisticsUpdateFailure2(_logger, exc);
            }

            return publication;
        }

        public Task UpdateRuntimeStatistics(
            SiloAddress siloAddress,
            SiloRuntimeStatistics siloStats,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateRuntimeStatisticsInternal(siloAddress, siloStats);
            return Task.CompletedTask;
        }

        internal Task<DisseminationApplyResult> ApplyDisseminatedRuntimeStatisticsAsync(
            SiloAddress siloAddress,
            SiloRuntimeStatistics siloStats,
            CancellationToken cancellationToken) =>
            this.RunOrQueueTask(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(UpdateRuntimeStatisticsInternal(siloAddress, siloStats, isDisseminated: true));
                },
                cancellationToken);

        internal bool IsRuntimeStatisticsObsolete(SiloAddress siloAddress, long timestampTicks) =>
            _siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active
            || (_periodicStats.TryGetValue(siloAddress, out var old) && old.DateTime.Ticks > timestampTicks);

        internal Dictionary<SiloAddress, SiloStatus> GetActiveSiloStatusesForStatisticsDigest() =>
            _siloStatusOracle.GetApproximateSiloStatuses(onlyActive: true);

        internal async Task<bool> TryPublishStatisticsViaDissemination(
            SiloRuntimeStatistics myStats,
            CancellationToken cancellationToken) =>
            (await PublishStatisticsViaDissemination(myStats, cancellationToken)).Receipt.Accepted;

        private async Task<StatisticsPublication> PublishStatisticsViaDissemination(
            SiloRuntimeStatistics myStats,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disseminationPublicationTask is { IsCompleted: false } pending)
            {
                return await AwaitSharedPublication(pending, cancellationToken);
            }

            return await (_disseminationPublicationTask = PublishStatisticsViaDisseminationCore(myStats, cancellationToken));
        }

        private static async Task<StatisticsPublication> AwaitSharedPublication(
            Task<StatisticsPublication> publication,
            CancellationToken cancellationToken)
        {
            try
            {
                return await publication.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (publication.IsCompletedSuccessfully)
                {
                    return await publication;
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        private async Task<StatisticsPublication> PublishStatisticsViaDisseminationCore(
            SiloRuntimeStatistics myStats,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_statisticsRefreshTime <= TimeSpan.Zero)
            {
                return default;
            }

            var disseminationNamespace = _serviceProvider.GetRequiredService<DeploymentLoadStatisticsDisseminationNamespace>();
            if (!disseminationNamespace.Options.Enabled)
            {
                return default;
            }

            var timeProvider = _serviceProvider.GetRequiredService<TimeProvider>();
            // A cohort may intentionally stay open for P. Allow another P for transport and
            // distribution admission; the receipt's delay, not this budget, controls sampling.
            // For long periods, the ordinary RPC timeout may expire first and take the same
            // logged direct-publication fallback without changing cluster-wide RPC settings.
            var receiptTimeout = TimeSpan.FromMilliseconds(Math.Min(disseminationNamespace.AggregationPeriod.TotalMilliseconds * 2, uint.MaxValue - 1));
            using var timeoutCancellation = new CancellationTokenSource(receiptTimeout, timeProvider);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            try
            {
                var dissemination = _serviceProvider.GetRequiredService<IDisseminationService>();
                var receipt = await dissemination.PublishAggregated(
                    disseminationNamespace,
                    _siloDetails.SiloAddress,
                    myStats.DateTime.Ticks,
                    cancellation.Token);
                var receiptTimestamp = timeProvider.GetTimestamp();
                return new(receipt, timeProvider, receiptTimestamp);
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                LogDebugRuntimeStatisticsDisseminationTimedOut(_logger, receiptTimeout);
                return default;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception exception)
            {
                LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                return default;
            }
        }

        private readonly record struct StatisticsPublication(
            DisseminationPublicationReceipt Receipt,
            TimeProvider? TimeProvider,
            long ReceiptTimestamp);

        private async Task PublishStatisticsDirectly(
            SiloRuntimeStatistics myStats,
            IReadOnlyCollection<SiloAddress> members,
            CancellationToken cancellationToken)
        {
            var tasks = new List<Task>(members.Count);
            foreach (var siloAddress in members)
            {
                if (siloAddress.Equals(_siloDetails.SiloAddress))
                {
                    continue;
                }

                try
                {
                    var deploymentLoadPublisher = _grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
                        Constants.DeploymentLoadPublisherSystemTargetType, siloAddress);
                    tasks.Add(deploymentLoadPublisher.UpdateRuntimeStatistics(_siloDetails.SiloAddress, myStats, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    tasks.Add(Task.FromCanceled(cancellationToken));
                }
                catch (Exception exception)
                {
                    LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                }
            }

            await Task.WhenAll(tasks);
        }

        private DisseminationApplyResult UpdateRuntimeStatisticsInternal(
            SiloAddress siloAddress,
            SiloRuntimeStatistics siloStats,
            bool isDisseminated = false)
        {
            LogTraceUpdateRuntimeStatistics(_logger, siloAddress);
            if (_siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
            {
                return DisseminationApplyResult.Rejected;
            }

            // Take only if newer.
            if (_periodicStats.TryGetValue(siloAddress, out var old))
            {
                if (old.DateTime > siloStats.DateTime)
                {
                    return DisseminationApplyResult.Obsolete;
                }

                if (isDisseminated && old.DateTime == siloStats.DateTime)
                {
                    return DisseminationApplyResult.Duplicate;
                }
            }

            _periodicStats[siloAddress] = siloStats;
            NotifyAllStatisticsChangeEventsSubscribers(siloAddress, siloStats);
            DeploymentLoadPublisherEvents.EmitReceived(siloAddress, _siloDetails.SiloAddress, siloStats);
            return DisseminationApplyResult.Applied;
        }

        internal async Task RefreshClusterStatistics(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceRefreshStatistics(_logger);
            await this.RunOrQueueTask(() =>
                {
                    var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                    var tasks = new List<Task>(members.Count);
                    foreach (var siloAddress in members)
                    {
                        tasks.Add(RefreshSiloStatistics(siloAddress, cancellationToken));
                    }

                    return Task.WhenAll(tasks);
                });
        }

        private async Task RefreshSiloStatistics(SiloAddress silo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var statistics = await _grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo)
                    .GetRuntimeStatistics(cancellationToken);
                UpdateRuntimeStatisticsInternal(silo, statistics);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogWarningRuntimeStatisticsUpdateFailure3(_logger, exception, silo);
            }
        }

        public bool SubscribeToStatisticsChangeEvents(ISiloStatisticsChangeListener observer)
        {
            lock (_siloStatisticsChangeListeners)
            {
                if (_siloStatisticsChangeListeners.Contains(observer)) return false;

                _siloStatisticsChangeListeners.Add(observer);
                return true;
            }
        }

        public bool UnsubscribeStatisticsChangeEvents(ISiloStatisticsChangeListener observer)
        {
            lock (_siloStatisticsChangeListeners)
            {
                return _siloStatisticsChangeListeners.Remove(observer);
            }
        }

        private void NotifyAllStatisticsChangeEventsSubscribers(SiloAddress silo, SiloRuntimeStatistics? stats)
        {
            ISiloStatisticsChangeListener[] subscribers;
            lock (_siloStatisticsChangeListeners)
            {
                subscribers = [.. _siloStatisticsChangeListeners];
            }

            ExceptionDispatchInfo? failure = null;
            foreach (var subscriber in subscribers)
            {
                try
                {
                    if (stats == null)
                    {
                        subscriber.RemoveSilo(silo);
                    }
                    else
                    {
                        subscriber.SiloStatisticsChangeNotification(silo, stats);
                    }
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            failure?.Throw();
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            WorkItemGroup.QueueAction(() =>
            {
                Utils.SafeExecute(() => OnSiloStatusChange(updatedSilo, status), _logger);
            });
        }

        private void OnSiloStatusChange(SiloAddress updatedSilo, SiloStatus status)
        {
            if (!status.IsTerminating()) return;

            DeploymentLoadPublisherEvents.EmitRemoved(updatedSilo, _siloDetails.SiloAddress);
            _periodicStats.TryRemove(updatedSilo, out _);
            NotifyAllStatisticsChangeEventsSubscribers(updatedSilo, null);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(
                nameof(DeploymentLoadPublisher),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartAsync,
                DisposePublishTimer);

            Task DisposePublishTimer(CancellationToken ct)
            {
                _publishTimer?.Dispose();
                return Task.CompletedTask;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting DeploymentLoadPublisher"
        )]
        private static partial void LogDebugStartingDeploymentLoadPublisher(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Started DeploymentLoadPublisher"
        )]
        private static partial void LogDebugStartedDeploymentLoadPublisher(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Deployment load dissemination did not accept the update within {Timeout}. Publishing directly.")]
        private static partial void LogDebugRuntimeStatisticsDisseminationTimedOut(ILogger logger, TimeSpan timeout);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "PublishStatistics"
        )]
        private static partial void LogTracePublishStatistics(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_1,
            Level = LogLevel.Warning,
            Message = "An unexpected exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignored"
        )]
        private static partial void LogWarningRuntimeStatisticsUpdateFailure1(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_2,
            Level = LogLevel.Warning,
            Message = "An exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignoring"
        )]
        private static partial void LogWarningRuntimeStatisticsUpdateFailure2(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "UpdateRuntimeStatistics from {Server}"
        )]
        private static partial void LogTraceUpdateRuntimeStatistics(ILogger logger, SiloAddress server);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "RefreshStatistics"
        )]
        private static partial void LogTraceRefreshStatistics(ILogger logger);

        [LoggerMessage(
            EventId = (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_3,
            Level = LogLevel.Warning,
            Message = "An unexpected exception was thrown from RefreshStatistics by ISiloControl.GetRuntimeStatistics({SiloAddress}). Will keep using stale statistics."
        )]
        private static partial void LogWarningRuntimeStatisticsUpdateFailure3(ILogger logger, Exception exception, SiloAddress siloAddress);
    }
}
