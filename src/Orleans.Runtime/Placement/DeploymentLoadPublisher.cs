using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private IDisposable? _publishTimer;

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
            LogDebugStartingDeploymentLoadPublisher(_logger);

            if (_statisticsRefreshTime > TimeSpan.Zero)
            {
                // Randomize PublishStatistics timer,
                // but also upon start publish my stats to everyone and take everyone's stats for me to start with something.
                var randomTimerOffset = RandomTimeSpan.Next(_statisticsRefreshTime);
                _publishTimer = RegisterTimer(
                    static state => ((DeploymentLoadPublisher)state!).PublishStatistics(CancellationToken.None),
                    this,
                    randomTimerOffset,
                    _statisticsRefreshTime);
            }

            await RefreshClusterStatistics(cancellationToken);
            await PublishStatistics(cancellationToken);
            LogDebugStartedDeploymentLoadPublisher(_logger);
        }

        private async Task PublishStatistics(CancellationToken cancellationToken)
        {
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
                if (!await TryPublishStatisticsViaDissemination(myStats, members))
                {
                    await PublishStatisticsDirectly(myStats, members, cancellationToken);
                }

                DeploymentLoadPublisherEvents.EmitClusterRefreshed(_siloDetails.SiloAddress, _periodicStats);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                throw;
            }
            catch (Exception exc)
            {
                LogWarningRuntimeStatisticsUpdateFailure2(_logger, exc);
            }
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

        internal DisseminationApplyResult ApplyDisseminatedRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats) =>
            UpdateRuntimeStatisticsInternal(siloAddress, siloStats, rejectEqualTimestamp: true);

        internal bool IsRuntimeStatisticsObsolete(SiloAddress siloAddress, long timestampTicks)
        {
            if (_siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
            {
                return true;
            }

            return _periodicStats.TryGetValue(siloAddress, out var old) && old.DateTime.Ticks > timestampTicks;
        }

        internal Task RefreshSiloStatisticsForDissemination(SiloAddress silo) => RefreshSiloStatistics(silo);

        internal IReadOnlyCollection<SiloAddress> GetActiveSilosForDissemination() =>
            _siloStatusOracle.GetApproximateSiloStatuses(onlyActive: true).Keys;

        private async Task<bool> TryPublishStatisticsViaDissemination(SiloRuntimeStatistics myStats, IReadOnlyCollection<SiloAddress> members)
        {
            try
            {
                var dissemination = _serviceProvider.GetService<DisseminationService>();
                var topic = _serviceProvider.GetService<DeploymentLoadStatisticsDisseminationTopic>();
                if (dissemination is null || topic is null || !topic.IsEnabled)
                {
                    return false;
                }

                var item = topic.CreateItem(_siloDetails.SiloAddress, myStats);
                return await dissemination.Publish(topic.Name, item, targetPeers: null, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                return false;
            }
        }

        private async Task PublishStatisticsDirectly(
            SiloRuntimeStatistics myStats,
            IReadOnlyCollection<SiloAddress> members,
            CancellationToken cancellationToken)
        {
            var tasks = new List<Task>(members.Count);
            foreach (var siloAddress in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // No need to make a grain call to ourselves.
                if (siloAddress.Equals(_siloDetails.SiloAddress))
                {
                    continue;
                }

                try
                {
                    var deploymentLoadPublisher = _grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(Constants.DeploymentLoadPublisherSystemTargetType, siloAddress);
                    tasks.Add(deploymentLoadPublisher.UpdateRuntimeStatistics(_siloDetails.SiloAddress, myStats, cancellationToken));
                }
                catch (Exception exception)
                {
                    LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                }
            }

            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }

        private DisseminationApplyResult UpdateRuntimeStatisticsInternal(SiloAddress siloAddress, SiloRuntimeStatistics siloStats, bool rejectEqualTimestamp = false)
        {
            LogTraceUpdateRuntimeStatistics(_logger, siloAddress);
            if (_siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
            {
                return DisseminationApplyResult.Rejected;
            }

            // Take only if newer.
            if (_periodicStats.TryGetValue(siloAddress, out var old) && old.DateTime > siloStats.DateTime)
            {
                return DisseminationApplyResult.Obsolete;
            }

            if (rejectEqualTimestamp && old is not null && old.DateTime == siloStats.DateTime)
            {
                return DisseminationApplyResult.Duplicate;
            }

            _periodicStats[siloAddress] = siloStats;
            NotifyAllStatisticsChangeEventsSubscribers(siloAddress, siloStats);
            DeploymentLoadPublisherEvents.EmitReceived(siloAddress, _siloDetails.SiloAddress, siloStats);
            return DisseminationApplyResult.Applied;
        }

        internal async Task RefreshClusterStatistics(CancellationToken cancellationToken = default)
        {
            LogTraceRefreshStatistics(_logger);
            await this.RunOrQueueTask(() =>
                {
                    var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                    var tasks = new List<Task>(members.Count);
                    foreach (var siloAddress in members)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        tasks.Add(RefreshSiloStatistics(siloAddress, cancellationToken));
                    }

                    return Task.WhenAll(tasks).WaitAsync(cancellationToken);
                });
        }

        private async Task RefreshSiloStatistics(SiloAddress silo, CancellationToken cancellationToken)
        {
            try
            {
                var statistics = await _grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo)
                    .GetRuntimeStatistics(cancellationToken);
                UpdateRuntimeStatisticsInternal(silo, statistics);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
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
            lock (_siloStatisticsChangeListeners)
            {
                foreach (var subscriber in _siloStatisticsChangeListeners)
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
            }
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
                _publishTimer!.Dispose(); // Preserve the existing lifecycle contract that publishing is enabled.
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
