using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Timers;

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
        List<T> NextSelection(int newSelectionCount, List<T> existingSelection);
    }

    /// <summary>
    /// Selector using round robin algorithm
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal class  RoundRobinSelector<T> : IResourceSelector<T>
    {
        private ReadOnlyCollection<T> resources;
        private int lastSelection;
        public RoundRobinSelector(IEnumerable<T> resources)
        {
            this.resources = new ReadOnlyCollection<T>(resources.Distinct().ToList());
            this.lastSelection = new Random().Next(this.resources.Count);
        }

        /// <summary>
        /// Try to select certain count of resources from resource list, which doesn't overlap with existing resources
        /// </summary>
        /// <param name="newSelectionCount"></param>
        /// <param name="existingSelection"></param>
        /// <returns></returns>
        public List<T> NextSelection(int newSelectionCount, List<T> existingSelection)
        {
            var selection = new List<T>(Math.Min(newSelectionCount, this.resources.Count));
            int tries = 0;
            while (selection.Count < newSelectionCount && tries++ < this.resources.Count)
            {
                this.lastSelection = (++this.lastSelection) % (this.resources.Count);
                if(!existingSelection.Contains(this.resources[this.lastSelection]))
                    selection.Add(this.resources[this.lastSelection]);
            }
            return selection;
        }
    }

    /// <summary>
    /// LeaseBasedQueueBalancer. This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
    /// as much as possible. 
    /// </summary>
    public class LeaseBasedQueueBalancer : QueueBalancerBase, IStreamQueueBalancer, IDisposable
    {
        /// <summary>
        /// Lease category for LeaseBasedQueueBalancer
        /// </summary>
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
        private bool isStarting;
        private IDisposable renewLeaseTimer;
        private IDisposable tryAcquireMaximumLeaseTimer;
        private IResourceSelector<QueueId> queueSelector;
        private int minimumResponsibility;
        private int maximumResponsibility;
        private IServiceProvider serviceProvider;
        private ILogger logger;
        private ILoggerFactory loggerFactory;
        private readonly LeaseBasedQueueBalancerOptions options;
        /// <summary>
        /// Constructor
        /// </summary>
        public LeaseBasedQueueBalancer(string name, LeaseBasedQueueBalancerOptions options, IServiceProvider serviceProvider, ISiloStatusOracle siloStatusOracle, IDeploymentConfiguration deploymentConfig, ILoggerFactory loggerFactory)
        {
            this.serviceProvider = serviceProvider;
            this.deploymentConfig = deploymentConfig;
            this.siloStatusOracle = siloStatusOracle;
            this.myQueues = new List<AcquiredQueue>();
            this.isStarting = true;
            this.loggerFactory = loggerFactory;
            this.options = options;
            this.logger = loggerFactory.CreateLogger($"{typeof(LeaseBasedQueueBalancer).FullName}-{name}");
        }

        public static IStreamQueueBalancer Create(IServiceProvider services, string name, IDeploymentConfiguration deploymentConfiguration)
        {
            var options = services.GetRequiredService<IOptionsSnapshot<LeaseBasedQueueBalancerOptions>>().Get(name);
            return ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancer>(services, name, options, deploymentConfiguration);
        }
        /// <inheritdoc/>
        public override Task Initialize(IStreamQueueMapper queueMapper)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            this.allQueues = new ReadOnlyCollection<QueueId>(queueMapper.GetAllQueues().ToList());
            if (this.allQueues.Count == 0)
                return Task.CompletedTask;
            this.leaseProvider = this.serviceProvider.GetRequiredService(options.LeaseProviderType) as ILeaseProvider;
            NotifyAfterStart().Ignore();
            //make lease renew frequency to be every half of lease time, to avoid renew failing due to timing issues, race condition or clock difference. 
            ITimerRegistry timerRegistry = this.serviceProvider.GetRequiredService<ITimerRegistry>();
            this.renewLeaseTimer = timerRegistry.RegisterTimer(null, this.MaintainAndBalanceQueues, null, this.options.SiloMaturityPeriod, this.options.LeaseLength.Divide(2));
            //try to acquire maximum leases every leaseLength 
            this.tryAcquireMaximumLeaseTimer = timerRegistry.RegisterTimer(null, this.AcquireLeaseToMeetMaxResponsibility, null, this.options.SiloMaturityPeriod, this.options.SiloMaturityPeriod);
            //Selector default to round robin selector now, but we can make a further change to make selector configurable if needed.  Selector algorithm could 
            //be affecting queue balancing stablization time in cluster initializing and auto-scaling
            this.queueSelector = new RoundRobinSelector<QueueId>(this.allQueues);
            return MaintainAndBalanceQueues(null);
        }

        /// <inheritdoc/>
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
            // step 2: if after renewing leases, myQueues count doesn't fall in [minimumResponsibility, maximumResponsibility] range, act accordingly
            if (this.myQueues.Count < this.minimumResponsibility)
            {
                await this.AcquireLeasesToMeetMinResponsibility();
            }
            else if (this.myQueues.Count > this.maximumResponsibility)
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
            var queueCountToRelease = this.myQueues.Count - this.maximumResponsibility;
            if (queueCountToRelease <= 0)
                return;
            var queuesToGiveUp = this.myQueues.GetRange(0, queueCountToRelease);
            await this.leaseProvider.Release(LeaseCategory, queuesToGiveUp.Select(queue => queue.AcquiredLease).ToArray());
            //remove queuesToGiveUp from myQueue list after the balancer released the leases on them
            this.myQueues.RemoveRange(0, queueCountToRelease);
            this.logger.Info($"Released leases for {queueCountToRelease} queues");
            this.logger.LogInformation($"I now own leases for {this.myQueues.Count} of an expected {this.minimumResponsibility} to {this.maximumResponsibility} queues.");
        }

        private Task AcquireLeaseToMeetMaxResponsibility(object state)
        {
            return AcquireLeasesToMeetExpectation(this.maximumResponsibility);
        }

        private Task AcquireLeasesToMeetMinResponsibility()
        {
            return AcquireLeasesToMeetExpectation(this.minimumResponsibility);
        }

        private async Task AcquireLeasesToMeetExpectation(int expectedTotalLeaseCount)
        {
            int maxAttempts = 5;
            int attempts = 0;
            int leasesToAquire = expectedTotalLeaseCount - this.myQueues.Count;
            if (leasesToAquire <= 0) return;
            while (attempts ++ < maxAttempts && leasesToAquire > 0)
            {
                this.logger.LogDebug($"I have {this.myQueues.Count} queues.  Trying to acquire {leasesToAquire} queues to reach {expectedTotalLeaseCount}");
                leasesToAquire = expectedTotalLeaseCount - this.myQueues.Count;
                //select new queues to acquire
                List<QueueId> expectedQueues = this.queueSelector.NextSelection(leasesToAquire, this.myQueues.Select(queue=>queue.QueueId).ToList()).ToList();
                IEnumerable<LeaseRequest> leaseRequests = expectedQueues.Select(queue => new LeaseRequest() {
                    ResourceKey = queue.ToString(),
                    Duration = this.options.LeaseLength
                });
                AcquireLeaseResult[] results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
                //add successfully acquired queue to myQueues list
                for (int i = 0; i < results.Length; i++)
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

            this.logger.LogInformation($"I now own leases for {this.myQueues.Count} of an expected {this.minimumResponsibility} to {this.maximumResponsibility} queues");
        }
        
        private async Task RenewLeases()
        {
            if (this.myQueues.Count <= 0)
                return;
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
            this.logger.LogInformation($"Renewed leases for {this.myQueues.Count} queues.");
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
                activeBuckets = GetActiveSiloCount(this.siloStatusOracle);
            }
            activeBuckets = Math.Max(1, activeBuckets);
            this.minimumResponsibility = this.allQueues.Count / activeBuckets;
            //if allQueues count is divisible by active bukets, then every bucket should take the same count of queues, otherwise, there should be one bucket take 1 more queue
            if (this.allQueues.Count % activeBuckets == 0)
                this.maximumResponsibility = this.minimumResponsibility;
            else this.maximumResponsibility = this.minimumResponsibility + 1;
        }


        private static int GetActiveSiloCount(ISiloStatusOracle siloStatusOracle)
        {
            return siloStatusOracle.GetApproximateSiloStatuses(true).Count;
        }

        private async Task NotifyAfterStart()
        {
            await Task.Delay(this.options.SiloMaturityPeriod);
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

        /// <inheritdoc/>
        public void Dispose()
        {
            this.renewLeaseTimer?.Dispose();
            this.renewLeaseTimer = null;
            this.tryAcquireMaximumLeaseTimer?.Dispose();
            this.tryAcquireMaximumLeaseTimer = null;
            //release all owned leases
            this.maximumResponsibility = 0;
            this.minimumResponsibility = 0;
            this.ReleaseLeasesToMeetResponsibility().Ignore();
        }
    }
}
