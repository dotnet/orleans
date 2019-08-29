using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Timers;

namespace Orleans.Streams
{
    /// <summary>
    /// IResourceSelector selects a certain amount of resources from a resource list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IResourceSelector<T>
    {
        /// <summary>
        /// Number of resources
        /// </summary>
        int Count { get; }

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
            var rand = new Random(this.GetHashCode());
            // distinct randomly ordered readonly collection
            this.resources = new ReadOnlyCollection<T>(resources.Distinct().OrderBy(_ => rand.Next()).ToList());
            this.lastSelection = rand.Next(this.resources.Count);
        }

        public int Count => this.resources.Count;

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
    /// LeaseBasedQueueBalancer. This balancer supports queue balancing in cluster auto-scale scenarios,
    /// unexpected server failure scenarios, and tries to support ideal distribution as much as possible. 
    /// </summary>
    public class LeaseBasedQueueBalancer : QueueBalancerBase, IStreamQueueBalancer
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

        private readonly LeaseBasedQueueBalancerOptions options;
        private readonly ILeaseProvider leaseProvider;
        private readonly ITimerRegistry timerRegistry;
        private readonly AsyncSerialExecutor executor;
        private ReadOnlyCollection<QueueId> allQueues;
        private List<AcquiredQueue> myQueues;
        private IDisposable leaseMaintenanceTimer;
        private IDisposable leaseMinAquisitionTimer;
        private IDisposable leaseMaxAquisitionTimer;
        private IResourceSelector<QueueId> queueSelector;
        private int minimumResponsibility;
        private int maximumResponsibility;

        /// <summary>
        /// Constructor
        /// </summary>
        public LeaseBasedQueueBalancer(string name, LeaseBasedQueueBalancerOptions options, ILeaseProvider leaseProvider, ITimerRegistry timerRegistry, IServiceProvider services, ILoggerFactory loggerFactory)
            : base(services, loggerFactory.CreateLogger($"{nameof(LeaseBasedQueueBalancer)}-{name}"))
        {
            this.options = options;
            this.leaseProvider = leaseProvider;
            this.timerRegistry = timerRegistry;
            this.executor = new AsyncSerialExecutor();
            this.myQueues = new List<AcquiredQueue>();
        }

        public static IStreamQueueBalancer Create(IServiceProvider services, string name)
        {
            var options = services.GetOptionsByName<LeaseBasedQueueBalancerOptions>(name);
            ILeaseProvider leaseProvider = services.GetServiceByName<ILeaseProvider>(name)
                ?? services.GetRequiredService<ILeaseProvider>();
            return ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancer>(services, name, options, leaseProvider);
        }

        /// <inheritdoc/>
        public override Task Initialize(IStreamQueueMapper queueMapper)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            this.allQueues = new ReadOnlyCollection<QueueId>(queueMapper.GetAllQueues().ToList());

            //Selector default to round robin selector now, but we can make a further change to make selector configurable if needed.  Selector algorithm could 
            //be affecting queue balancing stablization time in cluster initializing and auto-scaling
            this.queueSelector = new RoundRobinSelector<QueueId>(this.allQueues);
            return base.Initialize(queueMapper);
        }

        /// <inheritdoc/>
        public override async Task Shutdown()
        {
            this.myQueues.Clear();
            this.maximumResponsibility = 0;
            this.minimumResponsibility = 0;
            this.leaseMaintenanceTimer?.Dispose();
            this.leaseMaintenanceTimer = null;
            this.leaseMinAquisitionTimer?.Dispose();
            this.leaseMinAquisitionTimer = null;
            this.leaseMaxAquisitionTimer?.Dispose();
            this.leaseMaxAquisitionTimer = null;
            await base.Shutdown();
            //release all owned leases
            await this.executor.AddNext(this.ReleaseLeasesToMeetResponsibility);
        }

        /// <inheritdoc/>
        public override IEnumerable<QueueId> GetMyQueues()
        {
            return this.myQueues.Select(queue => queue.QueueId);
        }

        private async Task MaintainLeases(object state)
        {
            try
            {
                await this.executor.AddNext(this.MaintainLeases);
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "Maintaining leases failed");
            }
        }

        private async Task MaintainLeases()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            // step 1: renew existing leases 
            await this.RenewLeases();
            if (this.myQueues.Count > this.maximumResponsibility)
            {
                await this.ReleaseLeasesToMeetResponsibility();
            }
            await NotifyOnChange(oldQueues);
        }

        private async Task AcquireLeasesToMeetMaxResponsibility(object state)
        {
            try
            {
                await this.executor.AddNext(this.AcquireLeasesToMeetMaxResponsibility);
            } catch(Exception ex)
            {
                this.Logger.LogError(ex, "Acquiring max leases failed");
            }
        }

        private async Task AcquireLeasesToMeetMaxResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            if (this.myQueues.Count < this.maximumResponsibility)
            {
                await AcquireLeasesToMeetExpectation(this.maximumResponsibility);
            }
            else if (this.myQueues.Count > this.maximumResponsibility)
            {
                await this.ReleaseLeasesToMeetResponsibility();
            }
            await NotifyOnChange(oldQueues);
            if(this.myQueues.Count == this.maximumResponsibility)
            {
                this.leaseMaxAquisitionTimer?.Dispose();
                this.leaseMaxAquisitionTimer = null;
            }
        }

        private async Task AcquireLeasesToMeetMinResponsibility(object state)
        {
            try
            {
                await this.executor.AddNext(this.AcquireLeasesToMeetMinResponsibility);
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "Acquiring min leases failed");
            }
        }

        private async Task AcquireLeasesToMeetMinResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            if (this.myQueues.Count < this.minimumResponsibility)
            {
                await AcquireLeasesToMeetExpectation(this.minimumResponsibility);
            }
            else if (this.myQueues.Count > this.maximumResponsibility)
            {
                await this.ReleaseLeasesToMeetResponsibility();
            }
            await NotifyOnChange(oldQueues);
            if (this.myQueues.Count >= this.minimumResponsibility)
            {
                this.leaseMinAquisitionTimer?.Dispose();
                this.leaseMinAquisitionTimer = null;
            }
        }

        private async Task ReleaseLeasesToMeetResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (this.Logger.IsEnabled(LogLevel.Trace))
            {
                this.Logger.LogTrace("ReleaseLeasesToMeetResponsibility. QueueCount: {QueueCount}, MaximumResponsibility: {MaximumResponsibility}", this.myQueues.Count, this.maximumResponsibility);
            }
            int queueCountToRelease = this.myQueues.Count - this.maximumResponsibility;
            if (queueCountToRelease <= 0)
                return;
            AcquiredLease[] queuesToGiveUp = this.myQueues
                .GetRange(0, queueCountToRelease)
                .Select(queue => queue.AcquiredLease)
                .ToArray();
            await this.leaseProvider.Release(LeaseCategory, queuesToGiveUp);
            //remove queuesToGiveUp from myQueue list after the balancer released the leases on them
            this.myQueues.RemoveRange(0, queueCountToRelease);
            this.Logger.LogInformation("Released leases for {QueueCount} queues", queueCountToRelease);
            this.Logger.LogInformation("Holding leases for {QueueCount} of an expected {MinQueueCount} to {MaxQueueCount} queues.", this.myQueues.Count, this.minimumResponsibility, this.maximumResponsibility);
        }

        private async Task AcquireLeasesToMeetExpectation(int expectedTotalLeaseCount)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (this.Logger.IsEnabled(LogLevel.Trace))
            {
                this.Logger.LogTrace("AcquireLeasesToMeetExpectation. QueueCount: {QueueCount}, ExpectedTotalLeaseCount: {ExpectedTotalLeaseCount}", this.myQueues.Count, expectedTotalLeaseCount);
            }

            int leasesToAquire = expectedTotalLeaseCount - this.myQueues.Count;
            if (leasesToAquire <= 0) return;

            // tracks how many remaining possible leases there are.
            int possibleLeaseCount = this.queueSelector.Count - this.myQueues.Count;
            if (this.Logger.IsEnabled(LogLevel.Debug))
            {
                this.Logger.LogDebug("Holding leased for {QueueCount} queues.  Trying to acquire {AquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount}", this.myQueues.Count, leasesToAquire, expectedTotalLeaseCount, possibleLeaseCount);
            }

            // try to acquire leases until we have no more to aquire or no more possible
            while (!base.Cancellation.IsCancellationRequested && leasesToAquire > 0 && possibleLeaseCount > 0)
            {
                //select new queues to acquire
                List<QueueId> expectedQueues = this.queueSelector.NextSelection(leasesToAquire, this.myQueues.Select(queue=>queue.QueueId).ToList());
                // build lease request from each queue
                LeaseRequest[] leaseRequests = expectedQueues
                    .Select(queue => new LeaseRequest() {
                        ResourceKey = queue.ToString(),
                        Duration = this.options.LeaseLength
                    })
                    .ToArray();

                AcquireLeaseResult[] results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests);
                //add successfully acquired queue to myQueues list
                for (int i = 0; i < results.Length; i++)
                {
                    AcquireLeaseResult result = results[i];
                    switch (result.StatusCode)
                    {
                        case ResponseCode.OK:
                            {
                                this.myQueues.Add(new AcquiredQueue(expectedQueues[i], result.AcquiredLease));
                                break;
                            }
                        case ResponseCode.TransientFailure:
                            {
                                this.Logger.LogWarning(result.FailureException, "Failed to aquire lease {LeaseKey} due to transient error.", result.AcquiredLease.ResourceKey);
                                break;
                            }
                        // this is expected much of the time
                        case ResponseCode.LeaseNotAvailable:
                            {
                                if (this.Logger.IsEnabled(LogLevel.Debug))
                                {
                                    this.Logger.LogDebug(result.FailureException, "Failed to aquire lease {LeaseKey} due to {Reason}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                                }
                                break;
                            }
                        // An acquire call should not return this code, so log as error
                        case ResponseCode.InvalidToken:
                            {
                                this.Logger.LogError(result.FailureException, "Failed to aquire lease {LeaseKey} unexpected invalid token.", result.AcquiredLease.ResourceKey);
                                break;
                            }
                    }
                }
                possibleLeaseCount -= expectedQueues.Count;
                leasesToAquire = expectedTotalLeaseCount - this.myQueues.Count;
                if (this.Logger.IsEnabled(LogLevel.Debug))
                {
                    this.Logger.LogDebug("Holding leased for {QueueCount} queues.  Trying to acquire {AquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount} lease", this.myQueues.Count, leasesToAquire, expectedTotalLeaseCount, possibleLeaseCount);
                }
            }

            this.Logger.LogInformation("Holding leases for {QueueCount} of an expected {MinQueueCount} to {MaxQueueCount} queues.", this.myQueues.Count, this.minimumResponsibility, this.maximumResponsibility);
        }
        
        private async Task RenewLeases()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (this.Logger.IsEnabled(LogLevel.Trace))
            {
                this.Logger.LogTrace("RenewLeases. QueueCount: {QueueCount}", this.myQueues.Count);
            }
            if (this.myQueues.Count <= 0)
                return;
            var results = await this.leaseProvider.Renew(LeaseCategory, this.myQueues.Select(queue => queue.AcquiredLease).ToArray());
            var updatedQueues = new List<AcquiredQueue>();
            //update myQueues list with successfully renewed leases
            for (int i = 0; i < results.Count(); i++)
            {
                AcquireLeaseResult result = results[i];
                switch (result.StatusCode)
                {
                    case ResponseCode.OK:
                        {
                            updatedQueues.Add(new AcquiredQueue(this.myQueues[i].QueueId, result.AcquiredLease));
                            break;
                        }
                    case ResponseCode.TransientFailure:
                        {
                            this.Logger.LogWarning(result.FailureException, "Failed to renew lease {LeaseKey} due to transient error.", result.AcquiredLease.ResourceKey);
                            break;
                        }
                    // these can occure if lease has expired and/or someone else has taken it
                    case ResponseCode.InvalidToken:
                    case ResponseCode.LeaseNotAvailable:
                        {
                            this.Logger.LogWarning(result.FailureException, "Failed to renew lease {LeaseKey} due to {Reason}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                }
            }
            this.myQueues.Clear();
            this.myQueues = updatedQueues;
            this.Logger.LogInformation("Renewed leases for {QueueCount} queues.", this.myQueues.Count);
        }

        private Task NotifyOnChange(HashSet<QueueId> oldQueues)
        {
            if (base.Cancellation.IsCancellationRequested) return Task.CompletedTask;
            var newQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            //if queue changed, notify listeners
            return !oldQueues.SetEquals(newQueues)
                ? NotifyListeners()
                : Task.CompletedTask;
        }

        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            ScheduleUpdateResponsibilities(activeSilos).Ignore();
        }

        private async Task ScheduleUpdateResponsibilities(HashSet<SiloAddress> activeSilos)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            try
            {
                await this.executor.AddNext(() => UpdateResponsibilities(activeSilos));
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "Updating Responsibilities");
            }
        }

        private async Task UpdateResponsibilities(HashSet<SiloAddress> activeSilos)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            int activeSiloCount = Math.Max(1, activeSilos.Count);
            this.minimumResponsibility = this.allQueues.Count / activeSiloCount;
            //if allQueues count is divisible by active bucket, then every bucket should take the same count of queues,
            //  otherwise, some buckets will need to take 1 more queue.
            this.maximumResponsibility = (this.allQueues.Count % activeSiloCount == 0)
                ? this.minimumResponsibility
                : this.minimumResponsibility + 1;
            if (this.options.Greedy)
            {
                this.minimumResponsibility = this.maximumResponsibility;
            }

            if (this.Logger.IsEnabled(LogLevel.Debug))
            {
                this.Logger.LogDebug("Updating Responsibilities for {QueueCount} queue over {SiloCount} silos. Need {MinQueueCount} to {MaxQueueCount} queues, have {MyQueueCount}",
                    this.allQueues.Count, activeSiloCount, this.minimumResponsibility, this.maximumResponsibility, this.myQueues.Count);
            }

            if (this.myQueues.Count < this.minimumResponsibility && this.leaseMinAquisitionTimer == null)
            {
                this.leaseMinAquisitionTimer = this.timerRegistry.RegisterTimer(null, this.AcquireLeasesToMeetMinResponsibility, null, this.options.MinLeaseAquisitionPeriod, this.options.MinLeaseAquisitionPeriod);
            }

            if (this.myQueues.Count != this.maximumResponsibility && this.leaseMaxAquisitionTimer == null)
            {
                this.leaseMaxAquisitionTimer = this.timerRegistry.RegisterTimer(null, this.AcquireLeasesToMeetMaxResponsibility, null, this.options.MaxLeaseAquisitionPeriod, this.options.MaxLeaseAquisitionPeriod);
            }

            if (this.leaseMaintenanceTimer == null)
            {
                this.leaseMaintenanceTimer = this.timerRegistry.RegisterTimer(null, this.MaintainLeases, null, this.options.LeaseRenewPeriod, this.options.LeaseRenewPeriod);
            }

            await AcquireLeasesToMeetMinResponsibility();
        }
    }
}
