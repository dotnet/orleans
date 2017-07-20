﻿using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;

namespace Orleans.Streams
{
    /// <summary>
    /// IResourceSelector selects a centain amount of resource from a resource list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IResourceSelector<T>
    {
        /// <summary>
        /// Try to select certain count of resources from resource list, which doesn't overlap with existing selection
        /// </summary>
        /// <param name="newSelectionCount"></param>
        /// <param name="existingSelection"></param>
        /// <returns></returns>
        IEnumerable<T> NextSelection(int newSelectionCount, IEnumerable<T> existingSelection);
    }

    /// <summary>
    /// Selector using round robin algorithm
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal class RoundRobinSelector<T> : IResourceSelector<T>
    {
        private List<T> resources;
        private int lastSelection;
        public RoundRobinSelector(List<T> resources)
        {
            this.resources = resources;
            this.lastSelection = new Random().Next(this.resources.Count);
        }

        /// <summary>
        /// Try to select certain count of resources from resource list, which doesn't overlap with existing resources
        /// </summary>
        /// <param name="newSelectionCount"></param>
        /// <param name="existingSelection"></param>
        /// <returns></returns>
        public IEnumerable<T> NextSelection(int newSelectionCount, IEnumerable<T> existingSelection)
        {
            var selection = new List<T>();
            while (selection.Count < newSelectionCount)
            {
                this.lastSelection = (++this.lastSelection) % (this.resources.Count - 1);
                if(!existingSelection.Contains(this.resources[this.lastSelection]))
                    selection.Add(this.resources[this.lastSelection]);
            }
            return selection;
        }
    }

    /// <summary>
    ///  Stream queue balancer that uses Azure deployment information for load balancing. 
    /// Requires silo running in Azure.
    /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
    /// </summary>
    public class AzureDeploymentLeaseBasedBalancer : LeaseBasedQueueBalancer
    {
        public AzureDeploymentLeaseBasedBalancer(ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider)
            : base(serviceProvider, siloStatusOracle, DeploymentBasedQueueBalancerUtils.CreateDeploymentConfigForAzure(serviceProvider),
                  serviceProvider.GetRequiredService<Factory<string, Logger>>())
        { }
    }

    /// <summary>
    /// Stream queue balancer that uses the cluster configuration to determine deployment information for load balancing.  
    /// This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
    /// </summary>
    public class ClusterConfigDeploymentLeaseBasedBalancer : LeaseBasedQueueBalancer
    {
        public ClusterConfigDeploymentLeaseBasedBalancer(IServiceProvider serviceProvider, ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration, Factory<string, Logger> loggerFac)
            : base(serviceProvider, siloStatusOracle, new StaticClusterDeploymentConfiguration(clusterConfiguration), loggerFac)
        { }
    }

    /// <summary>
    /// Config for LeaseBasedQueueBalancer. User need to add this config to its stream provder's IProviderConfiguration in order to use LeaseBasedQueueBalancer in the stream provider
    /// </summary>
    public class LeaseBasedQueueBalancerConfig
    {
        /// <summary>
        /// LeaseProviderType
        /// </summary>
        public Type LeaseProviderType { get; set; }
        /// <summary>
        /// LeaseProviderTypeName
        /// </summary>
        public const string LeaseProviderTypeName = nameof(LeaseProviderType);
        /// <summary>
        /// LeaseLength
        /// </summary>
        public TimeSpan LeaseLength { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>
        /// LeaseLengthName
        /// </summary>
        public const string LeaseLengthName = nameof(LeaseLengthName);
        /// <summary>
        /// Constructor
        /// </summary>
        public LeaseBasedQueueBalancerConfig()
        { }
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="providerConfig"></param>
        public LeaseBasedQueueBalancerConfig(IProviderConfiguration providerConfig)
        {
            string leaseLength;
            if (providerConfig.Properties.TryGetValue(LeaseLengthName, out leaseLength))
            {
                this.LeaseLength = ConfigUtilities.ParseTimeSpan(leaseLength,
                    "Invalid time value for the " + LeaseLengthName + " property in the provider config values.");
            }

            this.LeaseProviderType = providerConfig.GetTypeProperty(LeaseProviderTypeName, null);
            if (this.LeaseProviderType == null)
                throw new ArgumentOutOfRangeException(LeaseProviderTypeName, "LeaseProviderType not set");
        }

        /// <summary>
        /// Write properties to IProviderConfiguration's property bag
        /// </summary>
        /// <param name="properties"></param>
        public void WriterProperties(Dictionary<string, string> properties)
        {
            properties[LeaseLengthName] = ConfigUtilities.ToParseableTimeSpan(this.LeaseLength);
            if (this.LeaseProviderType != null)
                properties[LeaseProviderTypeName] = this.LeaseProviderType.AssemblyQualifiedName;
            else
                throw new ArgumentOutOfRangeException(LeaseProviderTypeName, "LeaseProviderType not set");
        }
    }
    /// <summary>
    /// LeaseBasedQueueBalancer. This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
    /// as much as possible. 
    /// </summary>
    public class LeaseBasedQueueBalancer : QueueBalancerBaseClass, ISiloStatusListener, IStreamQueueBalancer, IDisposable
    {
        public const string LeaseCategory = "QueueBalancer";
        private class AcquiredQueue 
        {
            public QueueId QueueId { get; set; }
            public AcquiredLease AcquiredLease { get; set; }
            public AcquiredQueue(QueueId queueId, AcquiredLease lease)
            {
                this.QueueId = queueId;
                this.AcquiredLease = lease;
            }
        }
        private ILeaseProvider leaseProvider;
        private IDeploymentConfiguration deploymentConfig;
        private readonly ISiloStatusOracle siloStatusOracle;
        private ReadOnlyCollection<QueueId> allQueues;
        private List<AcquiredQueue> myQueues;
        private TimeSpan siloMaturityPeriod;
        private bool isStarting;
        private readonly ConcurrentDictionary<SiloAddress, bool> immatureSilos;
        private TimeSpan leaseLength;
        private AsyncTaskSafeTimer renewLeaseTimer;
        private AsyncTaskSafeTimer tryAcquireMaximumLeaseTimer;
        private IResourceSelector<QueueId> queueSelector;
        private int minimumResponsibilty;
        private int maximumRespobsibility;
        private IServiceProvider serviceProvider;
        private Logger logger;

        public LeaseBasedQueueBalancer(IServiceProvider serviceProvider, ISiloStatusOracle siloStatusOracle, IDeploymentConfiguration deploymentConfig, Factory<string, Logger> loggerFac)
            : base()
        {
            this.serviceProvider = serviceProvider;
            this.deploymentConfig = deploymentConfig;
            this.siloStatusOracle = siloStatusOracle;
            this.myQueues = new List<AcquiredQueue>();
            this.immatureSilos = new ConcurrentDictionary<SiloAddress, bool>();
            this.isStarting = true;
            this.logger = loggerFac(this.GetType().Name);
            // register for notification of changes to silo status for any silo in the cluster
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
            // record all already active silos as already mature. 
            // Even if they are not yet, they will be mature by the time I mature myself (after I become !isStarting).
            foreach (var silo in siloStatusOracle.GetApproximateSiloStatuses(true).Keys.Where(s => !s.Equals(siloStatusOracle.SiloAddress)))
            {
                immatureSilos[silo] = false;     // record as mature
            }
        }

        public override Task Initialize(string strProviderName, IStreamQueueMapper queueMapper, TimeSpan siloMaturityPeriod, IProviderConfiguration providerConfig)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            var balancerConfig = new LeaseBasedQueueBalancerConfig(providerConfig);
            this.leaseProvider = this.serviceProvider.GetRequiredService(balancerConfig.LeaseProviderType) as ILeaseProvider;
            this.leaseLength = balancerConfig.LeaseLength;
            this.allQueues = new ReadOnlyCollection<QueueId>(queueMapper.GetAllQueues().ToList());
            this.siloMaturityPeriod = siloMaturityPeriod;
            NotifyAfterStart().Ignore();
            //make lease renew frequency to be every half of lease time, to avoid renew failing due to timing issues, race condition or clock difference. 
            this.renewLeaseTimer = new AsyncTaskSafeTimer(this.MaintainAndBalanceQueues, null, this.siloMaturityPeriod, this.leaseLength.Divide(2));
            //try to acquire maximum leases every leaseLength 
            this.tryAcquireMaximumLeaseTimer = new AsyncTaskSafeTimer(this.AcquireLeaseToMeetMaxResponsibilty, null, this.siloMaturityPeriod, this.leaseLength);
            //Selector default to round robin selector now, but we can make a further change to make selector configurable if needed.  Selector algorithm could 
            //be affecting queue balancing stablization time in cluster initializing and auto-scaling
            this.queueSelector = new RoundRobinSelector<QueueId>(this.allQueues.ToList());
            return MaintainAndBalanceQueues(null);
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            return this.myQueues.Select(queue => queue.QueueId);
        }

        private async Task MaintainAndBalanceQueues(object state)
        {
            CalculateResponsibility();
            var oldQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            // step 1: renew existing leases 
            await this.RenewLeases();
            // step 2: if after renewing leases, myQueues count doesn't fall in [minimumResponsibility, maximumResponsibilty] range, act accordingly
            if (this.myQueues.Count < this.minimumResponsibilty)
            {
                await this.AcquireLeasesToMeetMinResponsibility();
            }
            else if (this.myQueues.Count > this.maximumRespobsibility)
            {
                await this.ReleaseLeasesToMeetResponsibility();
            }
            var newQueues = new HashSet<QueueId>(this.myQueues.Select(queue=> queue.QueueId));
            //if queue changed, notify listeners
            if (!oldQueues.SetEquals(newQueues))
                await NotifyListeners();
        }

        private async Task ReleaseLeasesToMeetResponsibility()
        {
            var queueCountToRelease = this.myQueues.Count - this.maximumRespobsibility;
            if (queueCountToRelease <= 0)
                return;
            var queuesToGiveUp = this.myQueues.GetRange(0, queueCountToRelease);
            await this.leaseProvider.Release(LeaseCategory, queuesToGiveUp.Select(queue => queue.AcquiredLease).ToArray());
            //remove queuesToGiveUp from myQueue list after the balancer released the leases on them
            this.myQueues.RemoveRange(0, queueCountToRelease);
            this.logger.Info($"ReleaseLeasesToMeetResponsibility: released {queueCountToRelease} queues, current queue Count: {this.myQueues.Count}");
        }

        private Task AcquireLeaseToMeetMaxResponsibilty(object state)
        {
            return AcquireLeasesToMeetExpectation(this.maximumRespobsibility);
        }

        private Task AcquireLeasesToMeetMinResponsibility()
        {
            return AcquireLeasesToMeetExpectation(this.minimumResponsibilty);
        }

        private async Task AcquireLeasesToMeetExpectation(int expectedTotalLeaseCount)
        {
            int maxAttempts = 5;
            int attempts = 0;
            int leasesToAquire = expectedTotalLeaseCount - this.myQueues.Count;
            this.logger.Info($"AcquireLeasesToMeetExpectation : Try to acquire {leasesToAquire} queues");
            while (attempts ++ <= maxAttempts && leasesToAquire > 0)
            {
                //select new queues to acquire
                var expectedQueues = this.queueSelector.NextSelection(leasesToAquire, this.myQueues.Select(queue=>queue.QueueId)).ToList();
                var leaseRequests = expectedQueues.Select(queue => new LeaseRequest() {
                    ResourceKey = queue.ToString(),
                    Duration = this.leaseLength
                });
                var results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
                //add successfully acquired queue to myQueues list
                for (int i = 0; i < results.Count(); i++)
                {
                    if (results[i].StatusCode == ResponseCode.OK)
                    {
                        this.myQueues.Add(new AcquiredQueue(expectedQueues[i], results[i].AcquiredLease));
                    }
                }
                //if reached expectedTotalLeaseCount
                if (this.myQueues.Count >= expectedTotalLeaseCount)
                {
                    break;
                }
            }

            this.logger.Info($"AcquireLeasesToMeetExpectation: finished. Now own {this.myQueues.Count} queues. Used attemps : {attempts}, Current minimumReponsibility : {this.minimumResponsibilty}, current maximumResponsibility : {this.maximumRespobsibility}");
        }
        
        private async Task RenewLeases()
        {
            var results = await this.leaseProvider.Renew(LeaseCategory, this.myQueues.Select(queue => queue.AcquiredLease).ToArray());
            var updatedQueues = new List<AcquiredQueue>();
            //update myQueues list with successfully renewed leases
            for (int i = 0; i < results.Count(); i++)
            {
                var result = results[i];
                if (result.StatusCode == ResponseCode.OK)
                {
                    updatedQueues.Add(new AcquiredQueue(this.myQueues[i].QueueId, result.AcquiredLease));
                }
            }
            this.myQueues.Clear();
            this.myQueues = updatedQueues;
            this.logger.Info($"RenewLeases: finished, currently own queues : {this.myQueues.Count}, current minimumResponsibilty : {this.minimumResponsibilty}, current maximunResponsibilty : {this.maximumRespobsibility}");
        }

        private void CalculateResponsibility()
        {
            int activeBuckets = 0;
            if (isStarting)
            {
                activeBuckets = this.deploymentConfig.GetAllSiloNames().Count;
            }
            else
            {
                activeBuckets = GetActiveSilos(this.siloStatusOracle, this.immatureSilos).Count;
            }
            this.minimumResponsibilty = this.allQueues.Count / activeBuckets;
            //if allQueues count is divisible by active bukets, then every bucket should take the same count of queues, otherwise, there should be one bucket take 1 more queue
            if (this.allQueues.Count % activeBuckets == 0)
                this.maximumRespobsibility = this.minimumResponsibilty;
            else this.maximumRespobsibility = this.minimumResponsibilty + 1;
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (status == SiloStatus.Dead)
            {
                // just clean up garbage from immatureSilos.
                bool ignore;
                immatureSilos.TryRemove(updatedSilo, out ignore);
            }
            SiloStatusChangeNotification().Ignore();
        }

        private async Task SiloStatusChangeNotification()
        {
            this.logger.Info("SiloStatusChangeNotification received");
            List<Task> tasks = new List<Task>();
            // look at all currently active silos not including myself
            foreach (var silo in siloStatusOracle.GetApproximateSiloStatuses(true).Keys.Where(s => !s.Equals(siloStatusOracle.SiloAddress)))
            {
                bool ignore;
                if (!immatureSilos.TryGetValue(silo, out ignore))
                {
                    tasks.Add(RecordImmatureSilo(silo));
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        private async Task RecordImmatureSilo(SiloAddress updatedSilo)
        {
            immatureSilos[updatedSilo] = true;      // record as immature
            await Task.Delay(siloMaturityPeriod);
            immatureSilos[updatedSilo] = false;     // record as mature
        }

        private async Task NotifyAfterStart()
        {
            await Task.Delay(siloMaturityPeriod);
            this.isStarting = false;
            await NotifyListeners();
        }

        private Task NotifyListeners()
        {
            List<IStreamQueueBalanceListener> queueBalanceListenersCopy;
            lock (queueBalanceListeners)
            {
                queueBalanceListenersCopy = queueBalanceListeners.ToList(); // make copy
            }
            var notificatioTasks = new List<Task>(queueBalanceListenersCopy.Count);
            foreach (IStreamQueueBalanceListener listener in queueBalanceListenersCopy)
            {
                notificatioTasks.Add(listener.QueueDistributionChangeNotification());
            }
            return Task.WhenAll(notificatioTasks);
        }

        public void Dispose()
        {
            this.renewLeaseTimer?.Dispose();
            this.renewLeaseTimer = null;
            this.tryAcquireMaximumLeaseTimer?.Dispose();
            this.tryAcquireMaximumLeaseTimer = null;
            //release all owned leases
            this.maximumRespobsibility = 0;
            this.minimumResponsibilty = 0;
            this.ReleaseLeasesToMeetResponsibility().Ignore();
        }
    }
}
