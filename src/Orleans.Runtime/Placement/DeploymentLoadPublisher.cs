using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
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
        private readonly ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> _periodicStats;
        private readonly TimeSpan _statisticsRefreshTime;
        private readonly List<ISiloStatisticsChangeListener> _siloStatisticsChangeListeners;
        private readonly ILogger _logger;

        private long _lastUpdateDateTimeTicks;
        private IDisposable _publishTimer;

        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics => _periodicStats;

        public SiloRuntimeStatistics LocalRuntimeStatistics { get; private set; }

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
                    static state => ((DeploymentLoadPublisher)state).PublishStatistics(),
                    this,
                    randomTimerOffset,
                    _statisticsRefreshTime);
            }

            await RefreshClusterStatistics();
            await PublishStatistics();
            LogDebugStartedDeploymentLoadPublisher(_logger);
        }

        private async Task PublishStatistics()
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

                // Inform other cluster members about our refreshed statistics.
                var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                var tasks = new List<Task>(members.Count);
                foreach (var siloAddress in members)
                {
                    // No need to make a grain call to ourselves.
                    if (siloAddress == _siloDetails.SiloAddress)
                    {
                        continue;
                    }

                    try
                    {
                        var deploymentLoadPublisher = _grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(Constants.DeploymentLoadPublisherSystemTargetType, siloAddress);
                        tasks.Add(deploymentLoadPublisher.UpdateRuntimeStatistics(_siloDetails.SiloAddress, myStats));
                    }
                    catch (Exception exception)
                    {
                        LogWarningRuntimeStatisticsUpdateFailure1(_logger, exception);
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                LogWarningRuntimeStatisticsUpdateFailure2(_logger, exc);
            }
        }

        public Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            UpdateRuntimeStatisticsInternal(siloAddress, siloStats);
            return Task.CompletedTask;
        }

        private void UpdateRuntimeStatisticsInternal(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            LogTraceUpdateRuntimeStatistics(_logger, siloAddress);
            if (_siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
            {
                return;
            }

            // Take only if newer.
            if (_periodicStats.TryGetValue(siloAddress, out var old) && old.DateTime > siloStats.DateTime)
            {
                return;
            }

            _periodicStats[siloAddress] = siloStats;
            NotifyAllStatisticsChangeEventsSubscribers(siloAddress, siloStats);
        }

        internal async Task RefreshClusterStatistics()
        {
            LogTraceRefreshStatistics(_logger);
            await this.RunOrQueueTask(() =>
                {
                    var members = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                    var tasks = new List<Task>(members.Count);
                    foreach (var siloAddress in members)
                    {
                        tasks.Add(RefreshSiloStatistics(siloAddress));
                    }

                    return Task.WhenAll(tasks);
                });
        }

        private async Task RefreshSiloStatistics(SiloAddress silo)
        {
            try
            {
                var statistics = await _grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, silo).GetRuntimeStatistics();
                UpdateRuntimeStatisticsInternal(silo, statistics);
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

        private void NotifyAllStatisticsChangeEventsSubscribers(SiloAddress silo, SiloRuntimeStatistics stats)
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

            _periodicStats.TryRemove(updatedSilo, out _);
            NotifyAllStatisticsChangeEventsSubscribers(updatedSilo, null);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(
                nameof(DeploymentLoadPublisher),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartAsync,
                ct =>
            {
                _publishTimer.Dispose();
                return Task.CompletedTask;
            });
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
