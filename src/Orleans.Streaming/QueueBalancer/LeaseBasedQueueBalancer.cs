using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.LeaseProviders;
using Orleans.Runtime;
using Orleans.Timers;

namespace Orleans.Streams
{
    /// <summary>
    /// LeaseBasedQueueBalancer. This balancer supports queue balancing in cluster auto-scale scenarios,
    /// unexpected server failure scenarios, and tries to support ideal distribution as much as possible. 
    /// </summary>
    public class LeaseBasedQueueBalancer : QueueBalancerBase, IStreamQueueBalancer
    {
        private class AcquiredQueue 
        {
            public int LeaseOrder { get; set; }
            public QueueId QueueId { get; set; }
            public AcquiredLease AcquiredLease { get; set; }
            public AcquiredQueue(int order, QueueId queueId, AcquiredLease lease)
            {
                this.LeaseOrder = order;
                this.QueueId = queueId;
                this.AcquiredLease = lease;
            }
        }

        private readonly LeaseBasedQueueBalancerOptions options;
        private readonly ILeaseProvider leaseProvider;
        private readonly ITimerRegistry timerRegistry;
        private readonly AsyncSerialExecutor executor = new AsyncSerialExecutor();
        private int allQueuesCount;
        private readonly List<AcquiredQueue> myQueues = new List<AcquiredQueue>();
        private IDisposable leaseMaintenanceTimer;
        private IDisposable leaseAquisitionTimer;
        private RoundRobinSelector<QueueId> queueSelector;
        private int responsibility;
        private int leaseOrder;

        /// <summary>
        /// Initializes a new instance of the <see cref="LeaseBasedQueueBalancer"/> class.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="options">The options.</param>
        /// <param name="leaseProvider">The lease provider.</param>
        /// <param name="timerRegistry">The timer registry.</param>
        /// <param name="services">The services.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public LeaseBasedQueueBalancer(
            string name,
            LeaseBasedQueueBalancerOptions options,
            ILeaseProvider leaseProvider,
            ITimerRegistry timerRegistry,
            IServiceProvider services,
            ILoggerFactory loggerFactory)
            : base(services, loggerFactory.CreateLogger($"{typeof(LeaseBasedQueueBalancer).FullName}.{name}"))
        {
            this.options = options;
            this.leaseProvider = leaseProvider;
            this.timerRegistry = timerRegistry;
        }

        /// <summary>
        /// Creates a new <see cref="LeaseBasedQueueBalancer"/> instance.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The name.</param>
        /// <returns>The new <see cref="LeaseBasedQueueBalancer"/> instance.</returns>
        public static IStreamQueueBalancer Create(IServiceProvider services, string name)
        {
            var options = services.GetOptionsByName<LeaseBasedQueueBalancerOptions>(name);
            var leaseProvider = services.GetServiceByName<ILeaseProvider>(name)
                ?? services.GetRequiredService<ILeaseProvider>();
            return ActivatorUtilities.CreateInstance<LeaseBasedQueueBalancer>(services, name, options, leaseProvider);
        }

        /// <inheritdoc/>
        public override Task Initialize(IStreamQueueMapper queueMapper)
        {
            if (base.Cancellation.IsCancellationRequested) throw new InvalidOperationException("Cannot initialize a terminated balancer.");
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            var allQueues = queueMapper.GetAllQueues().ToList();
            this.allQueuesCount = allQueues.Count;

            //Selector default to round robin selector now, but we can make a further change to make selector configurable if needed.  Selector algorithm could 
            //be affecting queue balancing stablization time in cluster initializing and auto-scaling
            this.queueSelector = new RoundRobinSelector<QueueId>(allQueues);
            return base.Initialize(queueMapper);
        }

        /// <inheritdoc/>
        public override async Task Shutdown()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            this.myQueues.Clear();
            this.responsibility = 0;
            this.leaseMaintenanceTimer?.Dispose();
            this.leaseMaintenanceTimer = null;
            this.leaseAquisitionTimer?.Dispose();
            this.leaseAquisitionTimer = null;
            await base.Shutdown();
            //release all owned leases
            await this.executor.AddNext(this.ReleaseLeasesToMeetResponsibility);
        }

        /// <inheritdoc/>
        public override IEnumerable<QueueId> GetMyQueues()
        {
            if (base.Cancellation.IsCancellationRequested) throw new InvalidOperationException("Cannot aquire queues from a terminated balancer.");
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
            try
            {
                bool allLeasesRenewed = await this.RenewLeases();
                // if we lost some leases during renew after leaseAquisitionTimer stopped, restart it
                if (!allLeasesRenewed &&
                    this.leaseAquisitionTimer == null &&
                    !base.Cancellation.IsCancellationRequested)
                {
                    this.leaseAquisitionTimer = this.timerRegistry.RegisterTimer(null, this.AcquireLeasesToMeetResponsibility, null, TimeSpan.Zero, this.options.LeaseAquisitionPeriod);
                }
            }
            finally
            {
                await this.NotifyOnChange(oldQueues);
            }
        }

        private async Task AcquireLeasesToMeetResponsibility(object state)
        {
            try
            {
                await this.executor.AddNext(this.AcquireLeasesToMeetResponsibility);
            }
            catch (Exception ex)
            {
                this.Logger.LogError(ex, "Acquiring min leases failed");
            }
        }

        private async Task AcquireLeasesToMeetResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            try
            {
                if (this.myQueues.Count < this.responsibility)
                {
                    await this.AcquireLeasesToMeetExpectation(this.responsibility, this.options.LeaseLength.Divide(10));
                }
                else if (this.myQueues.Count > this.responsibility)
                {
                    await this.ReleaseLeasesToMeetResponsibility();
                }
            }
            finally
            {
                await this.NotifyOnChange(oldQueues);
                if (this.myQueues.Count == this.responsibility)
                {
                    this.leaseAquisitionTimer?.Dispose();
                    this.leaseAquisitionTimer = null;
                }
            }
        }

        private async Task ReleaseLeasesToMeetResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (this.Logger.IsEnabled(LogLevel.Trace))
            {
                this.Logger.LogTrace("ReleaseLeasesToMeetResponsibility. QueueCount: {QueueCount}, Responsibility: {Responsibility}", this.myQueues.Count, this.responsibility);
            }
            var queueCountToRelease = this.myQueues.Count - this.responsibility;
            if (queueCountToRelease <= 0)
                return;
            // Remove oldest acquired queues first, this provides max recovery time for the queues
            //  being moved.
            // TODO: Consider making this behavior configurable/plugable - jbragg
            AcquiredLease[] queuesToGiveUp = this.myQueues
                .OrderBy(queue => queue.LeaseOrder)
                .Take(queueCountToRelease)
                .Select(queue => queue.AcquiredLease)
                .ToArray();
            // Remove queues from list even if release fails, since we can let the lease expire
            // TODO: mark for removal instead so we don't renew, and only remove leases that have not expired. - jbragg
            for(int index = this.myQueues.Count-1; index >= 0; index--)
            {
                if(queuesToGiveUp.Contains(this.myQueues[index].AcquiredLease))
                {
                    this.myQueues.RemoveAt(index);
                }
            }
            await this.leaseProvider.Release(this.options.LeaseCategory, queuesToGiveUp);
            //remove queuesToGiveUp from myQueue list after the balancer released the leases on them
            this.Logger.LogInformation("Released leases for {QueueCount} queues", queueCountToRelease);
            this.Logger.LogInformation("Holding leases for {QueueCount} of an expected {MinQueueCount} queues.", this.myQueues.Count, this.responsibility);
        }

        private async Task AcquireLeasesToMeetExpectation(int expectedTotalLeaseCount, TimeSpan timeout)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (this.Logger.IsEnabled(LogLevel.Trace))
            {
                this.Logger.LogTrace("AcquireLeasesToMeetExpectation. QueueCount: {QueueCount}, ExpectedTotalLeaseCount: {ExpectedTotalLeaseCount}", this.myQueues.Count, expectedTotalLeaseCount);
            }

            var leasesToAquire = expectedTotalLeaseCount - this.myQueues.Count;
            if (leasesToAquire <= 0) return;

            // tracks how many remaining possible leases there are.
            var possibleLeaseCount = this.queueSelector.Count - this.myQueues.Count;
            if (this.Logger.IsEnabled(LogLevel.Debug))
            {
                this.Logger.LogDebug("Holding leased for {QueueCount} queues.  Trying to acquire {AquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount}", this.myQueues.Count, leasesToAquire, expectedTotalLeaseCount, possibleLeaseCount);
            }

            ValueStopwatch sw = ValueStopwatch.StartNew();
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

                AcquireLeaseResult[] results = await this.leaseProvider.Acquire(this.options.LeaseCategory, leaseRequests);
                //add successfully acquired queue to myQueues list
                for (var i = 0; i < results.Length; i++)
                {
                    AcquireLeaseResult result = results[i];
                    switch (result.StatusCode)
                    {
                        case ResponseCode.OK:
                            {
                                this.myQueues.Add(new AcquiredQueue(this.leaseOrder++, expectedQueues[i], result.AcquiredLease));
                                break;
                            }
                        case ResponseCode.TransientFailure:
                            {
                                this.Logger.LogWarning(result.FailureException, "Failed to acquire lease {LeaseKey} due to transient error.", result.AcquiredLease.ResourceKey);
                                break;
                            }
                        // this is expected much of the time
                        case ResponseCode.LeaseNotAvailable:
                            {
                                if (this.Logger.IsEnabled(LogLevel.Debug))
                                {
                                    this.Logger.LogDebug(result.FailureException, "Failed to acquire lease {LeaseKey} due to {Reason}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                                }
                                break;
                            }
                        // An acquire call should not return this code, so log as error
                        case ResponseCode.InvalidToken:
                            {
                                this.Logger.LogError(result.FailureException, "Failed to aquire acquire {LeaseKey} unexpected invalid token.", result.AcquiredLease.ResourceKey);
                                break;
                            }
                        default:
                            {
                                this.Logger.LogError(result.FailureException, "Unexpected response to acquire request of lease {LeaseKey}.  StatusCode {StatusCode}.", result.AcquiredLease.ResourceKey, result.StatusCode);
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
                if (sw.Elapsed > timeout)
                {
                    // blown our alotted time, try again next period
                    break;
                }
            }

            this.Logger.LogInformation("Holding leases for {QueueCount} of an expected {MinQueueCount} queues.", this.myQueues.Count, this.responsibility);
        }

        /// <summary>
        /// Renew leases
        /// </summary>
        /// <returns>bool - false if we failed to renew all leases</returns>
        private async Task<bool> RenewLeases()
        {
            bool allRenewed = true;
            if (base.Cancellation.IsCancellationRequested) return false;
            if (this.Logger.IsEnabled(LogLevel.Trace))
            {
                this.Logger.LogTrace("RenewLeases. QueueCount: {QueueCount}", this.myQueues.Count);
            }
            if (this.myQueues.Count <= 0)
                return allRenewed;
            var results = await this.leaseProvider.Renew(this.options.LeaseCategory, this.myQueues.Select(queue => queue.AcquiredLease).ToArray());
            //update myQueues list with successfully renewed leases
            for (var i = 0; i < results.Length; i++)
            {
                AcquireLeaseResult result = results[i];
                switch (result.StatusCode)
                {
                    case ResponseCode.OK:
                        {
                            this.myQueues[i].AcquiredLease = result.AcquiredLease;
                            break;
                        }
                    case ResponseCode.TransientFailure:
                        {
                            this.myQueues.RemoveAt(i);
                            allRenewed &= false;
                            this.Logger.LogWarning(result.FailureException, "Failed to renew lease {LeaseKey} due to transient error.", result.AcquiredLease.ResourceKey);
                            break;
                        }
                    // these can occure if lease has expired and/or someone else has taken it
                    case ResponseCode.InvalidToken:
                    case ResponseCode.LeaseNotAvailable:
                        {
                            this.myQueues.RemoveAt(i);
                            allRenewed &= false;
                            this.Logger.LogWarning(result.FailureException, "Failed to renew lease {LeaseKey} due to {Reason}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                    default:
                        {
                            this.myQueues.RemoveAt(i);
                            allRenewed &= false;
                            this.Logger.LogError(result.FailureException, "Unexpected response to renew of lease {LeaseKey}.  StatusCode {StatusCode}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                }
            }
            this.Logger.LogInformation("Renewed leases for {QueueCount} queues.", this.myQueues.Count);
            return allRenewed;
        }

        private Task NotifyOnChange(HashSet<QueueId> oldQueues)
        {
            if (base.Cancellation.IsCancellationRequested) return Task.CompletedTask;
            var newQueues = new HashSet<QueueId>(this.myQueues.Select(queue => queue.QueueId));
            //if queue changed, notify listeners
            return !oldQueues.SetEquals(newQueues)
                ? this.NotifyListeners()
                : Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            this.ScheduleUpdateResponsibilities(activeSilos).Ignore();
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

        /// <summary>
        /// Checks to see if this balancer should be greedy, which means it attempts to grab one
        ///   more queue than the non-greedy balancers.
        /// </summary>
        /// <param name="overflow">number of free queues, assuming all balancers meet their minimum responsibilities</param>
        /// <param name="activeSilos">number of active silos hosting queues</param>
        /// <returns>bool - true indicates that the balancer should try to acquire one
        ///   more queue than the non-greedy balancers</returns>
        private bool AmGreedy(int overflow, HashSet<SiloAddress> activeSilos)
        {
            // If using multiple stream providers, this will select the same silos to be greedy for
            //   all providers, aggravating inbalance as stream provider count increases.
            // TODO: consider making this behavior configurable/plugable - jbragg
            // TODO: use heap? - jbragg
            return activeSilos.OrderBy(silo => silo)
                              .Take(overflow)
                              .Contains(base.SiloAddress);
        }

        private async Task UpdateResponsibilities(HashSet<SiloAddress> activeSilos)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var activeSiloCount = Math.Max(1, activeSilos.Count);
            this.responsibility = this.allQueuesCount / activeSiloCount;
            var overflow = this.allQueuesCount % activeSiloCount;
            if(overflow != 0 && this.AmGreedy(overflow, activeSilos))
            {
                this.responsibility++;
            }

            if (this.Logger.IsEnabled(LogLevel.Debug))
            {
                this.Logger.LogDebug("Updating Responsibilities for {QueueCount} queue over {SiloCount} silos. Need {MinQueueCount} queues, have {MyQueueCount}",
                    this.allQueuesCount, activeSiloCount, this.responsibility, this.myQueues.Count);
            }

            if (this.myQueues.Count < this.responsibility && this.leaseAquisitionTimer == null)
            {
                this.leaseAquisitionTimer = this.timerRegistry.RegisterTimer(
                    null,
                    this.AcquireLeasesToMeetResponsibility,
                    null,
                    this.options.LeaseAquisitionPeriod,
                    this.options.LeaseAquisitionPeriod);
            }

            if (this.leaseMaintenanceTimer == null)
            {
                this.leaseMaintenanceTimer = this.timerRegistry.RegisterTimer(
                    null,
                    this.MaintainLeases,
                    null,
                    this.options.LeaseRenewPeriod,
                    this.options.LeaseRenewPeriod);
            }

            await this.AcquireLeasesToMeetResponsibility();
        }
    }
}
