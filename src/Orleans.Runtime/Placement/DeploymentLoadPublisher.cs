using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    internal class DeploymentLoadPublisher : SystemTarget, IDeploymentLoadPublisher, ISiloStatusListener
    {
        private readonly ILocalSiloDetails _siloDetails;
        private readonly ISiloStatusOracle _siloStatusOracle;
        private readonly IInternalGrainFactory _grainFactory;
        private readonly ActivationDirectory _activationDirectory;
        private readonly IActivationWorkingSet _activationWorkingSet;
        private readonly IAppEnvironmentStatistics _appEnvironmentStatistics;
        private readonly IHostEnvironmentStatistics _hostEnvironmentStatistics;
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
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions)
            : base(Constants.DeploymentLoadPublisherSystemTargetType, siloDetails.SiloAddress, loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<DeploymentLoadPublisher>();
            _siloDetails = siloDetails;
            _siloStatusOracle = siloStatusOracle;
            _grainFactory = grainFactory;
            _activationDirectory = activationDirectory;
            _activationWorkingSet = activationWorkingSet;
            _appEnvironmentStatistics = appEnvironmentStatistics;
            _hostEnvironmentStatistics = hostEnvironmentStatistics;
            _loadSheddingOptions = loadSheddingOptions;
            _statisticsRefreshTime = options.Value.DeploymentLoadPublisherRefreshTime;
            _periodicStats = new ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>();
            _siloStatisticsChangeListeners = new List<ISiloStatisticsChangeListener>();
        }

        public async Task Start()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Starting DeploymentLoadPublisher");
            }

            if (_statisticsRefreshTime > TimeSpan.Zero)
            {
                // Randomize PublishStatistics timer,
                // but also upon start publish my stats to everyone and take everyone's stats for me to start with something.
                var randomTimerOffset = RandomTimeSpan.Next(_statisticsRefreshTime);
                _publishTimer = RegisterTimer(
                    static state => ((DeploymentLoadPublisher)state).PublishStatistics(),
                    this,
                    randomTimerOffset,
                    _statisticsRefreshTime,
                    "DeploymentLoadPublisher.PublishStatisticsTimer");
            }

            await RefreshClusterStatistics();
            await PublishStatistics();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Started DeploymentLoadPublisher");
            }
        }

        private async Task PublishStatistics()
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("PublishStatistics");
                }

                // Ensure that our timestamp is monotonically increasing.
                var ticks = _lastUpdateDateTimeTicks = Math.Max(_lastUpdateDateTimeTicks + 1, DateTime.UtcNow.Ticks);

                var myStats = new SiloRuntimeStatistics(
                    _activationDirectory.Count,
                    _activationWorkingSet.Count,
                    _appEnvironmentStatistics,
                    _hostEnvironmentStatistics,
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
                        _logger.LogWarning(
                            (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_1,
                            exception,
                            "An unexpected exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignored");
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                _logger.LogWarning(
                    (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_2,
                    exc,
                    "An exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignoring");
            }
        }

        public Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            UpdateRuntimeStatisticsInternal(siloAddress, siloStats);
            return Task.CompletedTask;
        }

        private void UpdateRuntimeStatisticsInternal(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("UpdateRuntimeStatistics from {Server}", siloAddress);
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
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RefreshStatistics");
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
                _logger.LogWarning(
                    (int)ErrorCode.Placement_RuntimeStatisticsUpdateFailure_3,
                    exception,
                    "An unexpected exception was thrown from RefreshStatistics by ISiloControl.GetRuntimeStatistics({SiloAddress}). Will keep using stale statistics.",
                    silo);
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

            if (Equals(updatedSilo, Silo))
            {
                _publishTimer.Dispose();
            }

            _periodicStats.TryRemove(updatedSilo, out _);
            NotifyAllStatisticsChangeEventsSubscribers(updatedSilo, null);
        }
    }
}
