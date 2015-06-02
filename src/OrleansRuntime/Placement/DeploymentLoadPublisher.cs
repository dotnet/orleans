/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Configuration;


namespace Orleans.Runtime
{
    /// <summary>
    /// This class collects runtime statistics for all silos in the current deployment for use by placement.
    /// </summary>
    internal class DeploymentLoadPublisher : SystemTarget, IDeploymentLoadPublisher, ISiloStatusListener
    {
        private readonly Silo silo;
        private readonly ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> periodicStats;
        private readonly TimeSpan statisticsRefreshTime;
        private readonly IList<ISiloStatisticsChangeListener> siloStatisticsChangeListeners;
        private readonly TraceLogger logger = TraceLogger.GetLogger("DeploymentLoadPublisher", TraceLogger.LoggerType.Runtime);

        public static DeploymentLoadPublisher Instance { get; private set; }

        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> PeriodicStatistics { get { return periodicStats; } }

        public static void CreateDeploymentLoadPublisher(Silo silo, GlobalConfiguration config)
        {
            Instance = new DeploymentLoadPublisher(silo, config.DeploymentLoadPublisherRefreshTime);
        }

        private DeploymentLoadPublisher(Silo silo, TimeSpan freshnessTime)
            : base(Constants.DeploymentLoadPublisherSystemTargetId, silo.SiloAddress)
        {
            this.silo = silo;
            statisticsRefreshTime = freshnessTime;
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
                var t = GrainTimer.FromTaskCallback(PublishStatistics, null, randomTimerOffset, statisticsRefreshTime);
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
                List<SiloAddress> members = silo.LocalSiloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
                var tasks = new List<Task>();
                var myStats = new SiloRuntimeStatistics(silo.Metrics, DateTime.UtcNow);
                foreach (var siloAddress in members)
                {
                    try
                    {
                        tasks.Add(GrainFactory.GetSystemTarget<IDeploymentLoadPublisher>(
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
            if (!silo.LocalSiloStatusOracle.GetApproximateSiloStatus(siloAddress).Equals(SiloStatus.Active))
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
                    List<SiloAddress> members = silo.LocalSiloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToList();
                    foreach (var siloAddress in members)
                    {
                        var capture = siloAddress;
                        Task task = GrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlId, capture)
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
