using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;


namespace Orleans.Runtime
{
    /// <summary>
    /// This class collects runtime statistics for all silos in the current deployment for use by placement.
    /// </summary>
    internal class DeploymentLoadPublisher : SystemTarget, IDeploymentLoadPublisher, ISiloStatusListener
    {
        private readonly Silo silo;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> periodicStats;
        private readonly TimeSpan statisticsRefreshTime;
        private readonly IList<ISiloStatisticsChangeListener> siloStatisticsChangeListeners;
        private readonly Logger logger = LogManager.GetLogger("DeploymentLoadPublisher", LoggerType.Runtime);
        
        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics { get { return periodicStats; } }

        public DeploymentLoadPublisher(
            Silo silo,
            ISiloStatusOracle siloStatusOracle,
            GlobalConfiguration config)
            : base(Constants.DeploymentLoadPublisherSystemTargetId, silo.SiloAddress)
        {
            this.silo = silo;
            this.siloStatusOracle = siloStatusOracle;
            statisticsRefreshTime = config.DeploymentLoadPublisherRefreshTime;
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
                var t = GrainTimer.FromTaskCallback(PublishStatistics, null, randomTimerOffset, statisticsRefreshTime, "DeploymentLoadPublisher.PublishStatisticsTimer");
                t.Start();
            }
            await RefreshStatistics();
            await PublishStatistics(null);
            logger.Info("Started DeploymentLoadPublisher.");
        }

        private async Task PublishStatistics(object _)
        {
            try
            {
                if(logger.IsVerbose) logger.Verbose("PublishStatistics.");
                List<SiloAddress> members = this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
                var tasks = new List<Task>();
                var myStats = new SiloRuntimeStatistics(silo.Metrics, DateTime.UtcNow);
                foreach (var siloAddress in members)
                {
                    try
                    {
                        tasks.Add(InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
                            Constants.DeploymentLoadPublisherSystemTargetId, siloAddress)
                            .UpdateRuntimeStatistics(silo.SiloAddress, myStats));
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
            if (logger.IsVerbose) logger.Verbose("UpdateRuntimeStatistics from {0}", siloAddress);
            if (this.siloStatusOracle.GetApproximateSiloStatus(siloAddress) != SiloStatus.Active)
                return TaskDone.Done;

            SiloRuntimeStatistics old;
            // Take only if newer.
            if (periodicStats.TryGetValue(siloAddress, out old) && old.DateTime > siloStats.DateTime)
                return TaskDone.Done;

            periodicStats[siloAddress] = siloStats;
            NotifyAllStatisticsChangeEventsSubscribers(siloAddress, siloStats);
            return TaskDone.Done;
        }

        internal async Task<ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>> RefreshStatistics()
        {
            if (logger.IsVerbose) logger.Verbose("RefreshStatistics.");
            await silo.LocalScheduler.RunOrQueueTask( () =>
                {
                    var tasks = new List<Task>();
                    List<SiloAddress> members = this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
                    foreach (var siloAddress in members)
                    {
                        var capture = siloAddress;
                        Task task = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlId, capture)
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
            if (!status.IsTerminating()) return;

            SiloRuntimeStatistics ignore;
            periodicStats.TryRemove(updatedSilo, out ignore);
            NotifyAllStatisticsChangeEventsSubscribers(updatedSilo, null);
        }
    }
}
