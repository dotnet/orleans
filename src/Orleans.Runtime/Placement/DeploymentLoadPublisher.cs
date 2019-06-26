using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Providers;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Utilities;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// This class collects runtime statistics for all silos in the current deployment for use by placement.
    /// </summary>
    internal class DeploymentLoadPublisher : SystemTarget, IDeploymentLoadPublisher, ISiloStatusListener, ILifecycleParticipant<ISiloLifecycle>, IDisposable
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
        private readonly TimeSpan statisticsRefreshTime;
        private readonly IList<ISiloStatisticsChangeListener> siloStatisticsChangeListeners;
        private readonly ILogger logger;
        private readonly IAsyncTimer publishTimer;

        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics { get; }

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
            IOptions<LoadSheddingOptions> loadSheddingOptions,
            IAsyncTimerFactory timerFactory,
            SiloProviderRuntime providerRuntime)
            : base(Constants.DeploymentLoadPublisherSystemTargetId, siloDetails.SiloAddress, loggerFactory)
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
            PeriodicStatistics = new ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>();
            siloStatisticsChangeListeners = new List<ISiloStatisticsChangeListener>();
            this.publishTimer = timerFactory.Create(statisticsRefreshTime, nameof(PublishStatistics));

            providerRuntime.RegisterSystemTarget(this);
        }

        private async Task PublishStatistics()
        {
            if (this.statisticsRefreshTime <= TimeSpan.Zero) return;

            logger.Info("Starting DeploymentLoadPublisher.");
            try
            {
                await RefreshStatistics();
            }
            catch (Exception exception)
            {
                this.logger.LogWarning("Exception while trying to refresh statistics from other silos: {Exception}", exception);
            }

            logger.Info("Started DeploymentLoadPublisher.");
            var periodOverride = new TimeSpan?(new SafeRandom().NextTimeSpan(this.statisticsRefreshTime));
            while (await this.publishTimer.NextTick(periodOverride))
            {
                periodOverride = default;
                try
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("PublishStatistics.");
                    List<SiloAddress> members = this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
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
                                Constants.DeploymentLoadPublisherSystemTargetId, siloAddress)
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
        }
        
        public Task UpdateRuntimeStatistics(SiloAddress siloAddress, SiloRuntimeStatistics siloStats)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("UpdateRuntimeStatistics from {0}", siloAddress);
            if (this.siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
                return Task.CompletedTask;

            SiloRuntimeStatistics old;
            // Take only if newer.
            if (PeriodicStatistics.TryGetValue(siloAddress, out old) && old.DateTime > siloStats.DateTime)
                return Task.CompletedTask;

            PeriodicStatistics[siloAddress] = siloStats;
            NotifyAllStatisticsChangeEventsSubscribers(siloAddress, siloStats);
            return Task.CompletedTask;
        }

        internal async Task<ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>> RefreshStatistics()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("RefreshStatistics.");
            await this.scheduler.RunOrQueueTask( () =>
                {
                    var tasks = new List<Task>();
                    List<SiloAddress> members = this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
                    foreach (var siloAddress in members)
                    {
                        var capture = siloAddress;
                        Task task = this.grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlId, capture)
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
                }, SchedulingContext);
            return PeriodicStatistics;
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

            SiloRuntimeStatistics ignore;
            PeriodicStatistics.TryRemove(updatedSilo, out ignore);
            NotifyAllStatisticsChangeEventsSubscribers(updatedSilo, null);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();
            lifecycle.Subscribe(nameof(DeploymentLoadPublisher), ServiceLifecycleStage.Active, OnActiveStart, OnActiveStop);

            Task OnActiveStart(CancellationToken ct)
            {
                tasks.Add(this.ScheduleTask(this.PublishStatistics));
                this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
                return Task.CompletedTask;
            }

            async Task OnActiveStop(CancellationToken ct)
            {
                this.siloStatusOracle.UnSubscribeFromSiloStatusEvents(this);
                this.publishTimer.Cancel();
                await Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(tasks));
            }
        }

        public void Dispose()
        {
            this.publishTimer.Dispose();
        }
    }
}
