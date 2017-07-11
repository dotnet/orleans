using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
            this.lastSelection = -1;
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
    /// LeaseBasedQueueBalancer. This balancer supports queue balancing in cluster auto-scale scenario, unexpected server failure scenario, and try to support ideal distribution 
    /// as much as possible. 
    /// </summary>
    public class LeaseBasedQueueBalancer : QueueBalancerBaseClass, ISiloStatusListener, IStreamQueueBalancer, IDisposable
    {
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
        private ISiloStatusOracle siloStatusOracle;
        private ReadOnlyCollection<QueueId> allQueues;
        private List<AcquiredQueue> myQueues;
        private TimeSpan siloMaturityPeriod;
        private bool isStarting;
        private ConcurrentDictionary<SiloAddress, bool> immatureSilos;
        private TimeSpan leaseLength = TimeSpan.FromMinutes(1);
        private AsyncTaskSafeTimer renewLeaseTimer;
        private IResourceSelector<QueueId> queueSelector;
        private int minimumResponsibilty;
        private int maximumRespobsibility;

        public LeaseBasedQueueBalancer(ILeaseProvider leaseProvider, ISiloStatusOracle siloStatusOracle, IDeploymentConfiguration deploymentConfig)
            : base()
        {
            this.leaseProvider = leaseProvider;
            this.deploymentConfig = deploymentConfig;
            this.siloStatusOracle = siloStatusOracle;
            this.myQueues = new List<AcquiredQueue>();
            this.immatureSilos = new ConcurrentDictionary<SiloAddress, bool>();
            this.isStarting = true;
            // register for notification of changes to silo status for any silo in the cluster
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
            // record all already active silos as already mature. 
            // Even if they are not yet, they will be mature by the time I mature myself (after I become !isStarting).
            foreach (var silo in siloStatusOracle.GetApproximateSiloStatuses(true).Keys.Where(s => !s.Equals(siloStatusOracle.SiloAddress)))
            {
                immatureSilos[silo] = false;     // record as mature
            }
        }

        public override Task Initialize(string strProviderName, IStreamQueueMapper queueMapper, TimeSpan siloMaturityPeriod)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            this.allQueues = new ReadOnlyCollection<QueueId>(queueMapper.GetAllQueues().ToList());
            this.siloMaturityPeriod = siloMaturityPeriod;
            NotifyAfterStart().Ignore();
            this.renewLeaseTimer = new AsyncTaskSafeTimer(this.MaintainAndBalanceQueues, null, this.siloMaturityPeriod, this.leaseLength);
            //Selector default to round robin selector now, but we can make a further change to make selector configurable if needed.  Selector algorithm could 
            //be affecting queue balancing stablization time in cluster initializing and auto-scaling
            this.queueSelector = new RoundRobinSelector<QueueId>(this.allQueues.ToList());
            return Task.CompletedTask;
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            return this.myQueues.Select(queue => queue.QueueId);
        }

        private async Task MaintainAndBalanceQueues(object state)
        {
            CalculateResponsibility();
            // step 1: renew existing leases 
            await this.RenewLeases();
            // step 2: if after renewing leases, myQueues count doesn't fall in [minimumResponsibility, maximumResponsibilty] range, act accordingly
            if (this.myQueues.Count < this.minimumResponsibilty)
            {
                await this.AcquireLeasesToMeetResponsibility();
            }
            else if (this.myQueues.Count > this.maximumRespobsibility)
            {
                await this.ReleaseLeasesToMeetResponsibility();
            }
        }

        private async Task ReleaseLeasesToMeetResponsibility()
        {
            var queueCountToRelease = this.myQueues.Count - this.maximumRespobsibility;
            var queuesToGiveUp = this.myQueues.GetRange(0, queueCountToRelease);
            await this.leaseProvider.Release(ResourceCategory.Streaming, queuesToGiveUp.Select(queue => queue.AcquiredLease).ToArray());
            //remove queuesToGiveUp from myQueue list after the balancer released the leases on them
            this.myQueues.RemoveRange(0, queueCountToRelease);
        }

        private async Task AcquireLeasesToMeetResponsibility()
        {
            int maxAttempts = 20;
            int maxLeaseToAquire = this.maximumRespobsibility - this.myQueues.Count;
            while (maxAttempts-- > 0)
            {
                //select new queues to acquire
                var expectedQueues = this.queueSelector.NextSelection(maxLeaseToAquire, this.myQueues.Select(queue=>queue.QueueId)).ToList();
                var leaseRequests = expectedQueues.Select(queue => new LeaseRequest() {
                    ResourceKey = queue.ToString(),
                    Duration = this.leaseLength
                });
                var results = await this.leaseProvider.Acquire(ResourceCategory.Streaming, leaseRequests.ToArray());
                //add successfully acquired queue to myQueues list
                for (int i = 0; i < results.Count(); i++)
                {
                    if (results[i].StatusCode == ResponseCode.OK)
                    {
                        this.myQueues.Add(new AcquiredQueue(expectedQueues[i], results[i].AcquiredLease));
                    }
                }
                //if reach minimum respobsibility, then return. Reach maximum respobsibility is a optional
                if (this.myQueues.Count >= this.minimumResponsibilty)
                    break;
            }
        }
        
        private async Task RenewLeases()
        {
            var results = await this.leaseProvider.Renew(ResourceCategory.Streaming, this.myQueues.Select(queue => queue.AcquiredLease).ToArray());
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
            if (this.allQueues.Count % this.minimumResponsibilty == 0)
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

            if (!isStarting)
            {
                await this.MaintainAndBalanceQueues(null);
                // notify, uncoditionaly
                NotifyListeners().Ignore();
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                await this.MaintainAndBalanceQueues(null);
                await NotifyListeners(); // notify, uncoditionaly
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
            this.isStarting = false;
            await Task.Delay(siloMaturityPeriod);
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
        }
    }
}
