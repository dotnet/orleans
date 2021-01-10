using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private readonly ILocalSiloDetails siloDetails;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly OrleansTaskScheduler scheduler;
        private readonly IMessageCenter messageCenter;
        private readonly ActivationDirectory activationDirectory;
        private readonly ActivationCollector activationCollector;
        private readonly IAppEnvironmentStatistics appEnvironmentStatistics;
        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;
        private readonly IOptions<LoadSheddingOptions> loadSheddingOptions;

        private readonly ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> periodicStats;
        private readonly TimeSpan statisticsRefreshTime;
        private readonly IList<ISiloStatisticsChangeListener> siloStatisticsChangeListeners;
        private readonly ILogger logger;
        private IDisposable publishTimer;

        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics { get { return periodicStats; } }

        public DeploymentLoadPublisher(
            ILocalSiloDetails siloDetails,
            ISiloStatusOracle siloStatusOracle,
            IOptions<DeploymentLoadPublisherOptions> options,
            IInternalGrainFactory grainFactory,
            OrleansTaskScheduler scheduler,
            ILoggerFactory loggerFactory,
            IMessageCenter messageCenter,
            ActivationDirectory activationDirectory,
            ActivationCollector activationCollector,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IOptions<LoadSheddingOptions> loadSheddingOptions)
            : base(Constants.DeploymentLoadPublisherSystemTargetType, siloDetails.SiloAddress, loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<DeploymentLoadPublisher>();
            this.siloDetails = siloDetails;
            this.siloStatusOracle = siloStatusOracle;
            this.grainFactory = grainFactory;
            this.scheduler = scheduler;
            this.messageCenter = messageCenter;
            this.activationDirectory = activationDirectory;
            this.activationCollector = activationCollector;
            this.appEnvironmentStatistics = appEnvironmentStatistics;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.loadSheddingOptions = loadSheddingOptions;
            statisticsRefreshTime = options.Value.DeploymentLoadPublisherRefreshTime;
            periodicStats = new ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>();
            siloStatisticsChangeListeners = new List<ISiloStatisticsChangeListener>();
        }

        public async Task Start()
        {
            logger.Info("Starting DeploymentLoadPublisher.");
            if (statisticsRefreshTime > TimeSpan.Zero)
            {
                var random = new SafeRandom();
                // Randomize PublishStatistics timer,
                // but also upon start publish my stats to everyone and take everyone's stats for me to start with something.
                var randomTimerOffset = random.NextTimeSpan(statisticsRefreshTime);
                this.publishTimer = this.RegisterTimer(PublishStatistics, null, randomTimerOffset, statisticsRefreshTime, "DeploymentLoadPublisher.PublishStatisticsTimer");
            }
            await RefreshStatistics();
            await PublishStatistics(null);
            logger.Info("Started DeploymentLoadPublisher.");
        }

        private async Task PublishStatistics(object _)
        {
            try
            {
                if(logger.IsEnabled(LogLevel.Debug)) logger.Debug("PublishStatistics.");
                var members = this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                var tasks = new List<Task>();
                var activationCount = this.activationDirectory.Count;
                var recentlyUsedActivationCount = this.activationCollector.GetNumRecentlyUsed(TimeSpan.FromMinutes(10));
                var myStats = new SiloRuntimeStatistics(
                    this.messageCenter,
                    activationCount,
                    recentlyUsedActivationCount,
                    this.appEnvironmentStatistics,
                    this.hostEnvironmentStatistics,
                    this.loadSheddingOptions,
                    DateTime.UtcNow);
                foreach (var siloAddress in members)
                {
                    try
                    {
                        tasks.Add(this.grainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
                            Constants.DeploymentLoadPublisherSystemTargetType, siloAddress)
                            .UpdateRuntimeStatistics(this.siloDetails.SiloAddress, myStats));
                    }
                    catch (Exception)
                    {
                        logger.Warn(ErrorCode.Placement_RuntimeStatisticsUpdateFailure_1,
                            String.Format("An unexpected exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignored."));
                    }
                }
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.Placement_RuntimeStatisticsUpdateFailure_2,
                    String.Format("An exception was thrown by PublishStatistics.UpdateRuntimeStatistics(). Ignoring."), exc);
            }
        }


        public Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("UpdateRuntimeStatistics from {0}", siloAddress);
            if (this.siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
                return Task.CompletedTask;

            SiloRuntimeStatistics old;
            // Take only if newer.
            if (periodicStats.TryGetValue(siloAddress, out old) && old.DateTime > siloStats.DateTime)
                return Task.CompletedTask;

            periodicStats[siloAddress] = siloStats;
            NotifyAllStatisticsChangeEventsSubscribers(siloAddress, siloStats);
            return Task.CompletedTask;
        }

        internal async Task<ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>> RefreshStatistics()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("RefreshStatistics.");
            await this.scheduler.RunOrQueueTask(() =>
                {
                    var tasks = new List<Task>();
                    var members = this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
                    foreach (var siloAddress in members)
                    {
                        var capture = siloAddress;
                        Task task = this.grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, capture)
                                .GetRuntimeStatistics()
                                .ContinueWith((Task<SiloRuntimeStatistics> statsTask) =>
                                    {
                                        if (statsTask.Status == TaskStatus.RanToCompletion)
                                        {
                                            UpdateRuntimeStatistics(capture, statsTask.Result);
                                        }
                                        else
                                        {
                                            logger.Warn(ErrorCode.Placement_RuntimeStatisticsUpdateFailure_3,
                                                String.Format("An unexpected exception was thrown from RefreshStatistics by ISiloControl.GetRuntimeStatistics({0}). Will keep using stale statistics.", capture),
                                                statsTask.Exception);
                                        }
                                    });
                        tasks.Add(task);
                        task.Ignore();
                    }
                    return Task.WhenAll(tasks);
                }, this);
            return periodicStats;
        }

        public bool SubscribeToStatisticsChangeEvents(ISiloStatisticsChangeListener observer)
        {
            lock (siloStatisticsChangeListeners)
            {
                if (siloStatisticsChangeListeners.Contains(observer)) return false;

                siloStatisticsChangeListeners.Add(observer);
                return true;
            }
        }

        public bool UnsubscribeStatisticsChangeEvents(ISiloStatisticsChangeListener observer)
        {
            lock (siloStatisticsChangeListeners)
            {
                return siloStatisticsChangeListeners.Contains(observer) && 
                    siloStatisticsChangeListeners.Remove(observer);
            }
        }

        private void NotifyAllStatisticsChangeEventsSubscribers(SiloAddress silo, SiloRuntimeStatistics stats)
        {
            lock (siloStatisticsChangeListeners) 
            {
                foreach (var subscriber in siloStatisticsChangeListeners)
                {
                    if (stats==null)
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
            this.ScheduleTask(() => { Utils.SafeExecute(() => this.OnSiloStatusChange(updatedSilo, status), this.logger); }).Ignore();
        }

        private void OnSiloStatusChange(SiloAddress updatedSilo, SiloStatus status)
        {
            if (!status.IsTerminating()) return;

            if (Equals(updatedSilo, this.Silo))
                this.publishTimer.Dispose();
            periodicStats.TryRemove(updatedSilo, out _);
            NotifyAllStatisticsChangeEventsSubscribers(updatedSilo, null);
        }
    }
}
