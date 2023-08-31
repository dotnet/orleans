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
                LeaseOrder = order;
                QueueId = queueId;
                AcquiredLease = lease;
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
                throw new ArgumentNullException(nameof(queueMapper));
            }
            var allQueues = queueMapper.GetAllQueues().ToList();
            allQueuesCount = allQueues.Count;

            //Selector default to round robin selector now, but we can make a further change to make selector configurable if needed.  Selector algorithm could 
            //be affecting queue balancing stablization time in cluster initializing and auto-scaling
            queueSelector = new RoundRobinSelector<QueueId>(allQueues);
            return base.Initialize(queueMapper);
        }

        /// <inheritdoc/>
        public override async Task Shutdown()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            myQueues.Clear();
            responsibility = 0;
            leaseMaintenanceTimer?.Dispose();
            leaseMaintenanceTimer = null;
            leaseAquisitionTimer?.Dispose();
            leaseAquisitionTimer = null;
            await base.Shutdown();
            //release all owned leases
            await executor.AddNext(ReleaseLeasesToMeetResponsibility);
        }

        /// <inheritdoc/>
        public override IEnumerable<QueueId> GetMyQueues()
        {
            if (base.Cancellation.IsCancellationRequested) throw new InvalidOperationException("Cannot aquire queues from a terminated balancer.");
            return myQueues.Select(queue => queue.QueueId);
        }

        private async Task MaintainLeases(object state)
        {
            try
            {
                await executor.AddNext(MaintainLeases);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Maintaining leases failed");
            }
        }

        private async Task MaintainLeases()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(myQueues.Select(queue => queue.QueueId));
            try
            {
                bool allLeasesRenewed = await RenewLeases();
                // if we lost some leases during renew after leaseAquisitionTimer stopped, restart it
                if (!allLeasesRenewed &&
                    leaseAquisitionTimer == null &&
                    !base.Cancellation.IsCancellationRequested)
                {
                    leaseAquisitionTimer = timerRegistry.RegisterTimer(null, AcquireLeasesToMeetResponsibility, null, TimeSpan.Zero, options.LeaseAquisitionPeriod);
                }
            }
            finally
            {
                await NotifyOnChange(oldQueues);
            }
        }

        private async Task AcquireLeasesToMeetResponsibility(object state)
        {
            try
            {
                await executor.AddNext(AcquireLeasesToMeetResponsibility);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Acquiring min leases failed");
            }
        }

        private async Task AcquireLeasesToMeetResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            var oldQueues = new HashSet<QueueId>(myQueues.Select(queue => queue.QueueId));
            try
            {
                if (myQueues.Count < responsibility)
                {
                    await AcquireLeasesToMeetExpectation(responsibility, options.LeaseLength.Divide(10));
                }
                else if (myQueues.Count > responsibility)
                {
                    await ReleaseLeasesToMeetResponsibility();
                }
            }
            finally
            {
                await NotifyOnChange(oldQueues);
                if (myQueues.Count == responsibility)
                {
                    leaseAquisitionTimer?.Dispose();
                    leaseAquisitionTimer = null;
                }
            }
        }

        private async Task ReleaseLeasesToMeetResponsibility()
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("ReleaseLeasesToMeetResponsibility. QueueCount: {QueueCount}, Responsibility: {Responsibility}", myQueues.Count, responsibility);
            }
            var queueCountToRelease = myQueues.Count - responsibility;
            if (queueCountToRelease <= 0)
                return;
            // Remove oldest acquired queues first, this provides max recovery time for the queues
            //  being moved.
            // TODO: Consider making this behavior configurable/plugable - jbragg
            AcquiredLease[] queuesToGiveUp = myQueues
                .OrderBy(queue => queue.LeaseOrder)
                .Take(queueCountToRelease)
                .Select(queue => queue.AcquiredLease)
                .ToArray();
            // Remove queues from list even if release fails, since we can let the lease expire
            // TODO: mark for removal instead so we don't renew, and only remove leases that have not expired. - jbragg
            for(int index = myQueues.Count-1; index >= 0; index--)
            {
                if(queuesToGiveUp.Contains(myQueues[index].AcquiredLease))
                {
                    myQueues.RemoveAt(index);
                }
            }
            await leaseProvider.Release(options.LeaseCategory, queuesToGiveUp);
            //remove queuesToGiveUp from myQueue list after the balancer released the leases on them
            Logger.LogInformation("Released leases for {QueueCount} queues", queueCountToRelease);
            Logger.LogInformation("Holding leases for {QueueCount} of an expected {MinQueueCount} queues.", myQueues.Count, responsibility);
        }

        private async Task AcquireLeasesToMeetExpectation(int expectedTotalLeaseCount, TimeSpan timeout)
        {
            if (base.Cancellation.IsCancellationRequested) return;
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("AcquireLeasesToMeetExpectation. QueueCount: {QueueCount}, ExpectedTotalLeaseCount: {ExpectedTotalLeaseCount}", myQueues.Count, expectedTotalLeaseCount);
            }

            var leasesToAquire = expectedTotalLeaseCount - myQueues.Count;
            if (leasesToAquire <= 0) return;

            // tracks how many remaining possible leases there are.
            var possibleLeaseCount = queueSelector.Count - myQueues.Count;
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Holding leased for {QueueCount} queues.  Trying to acquire {AquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount}", myQueues.Count, leasesToAquire, expectedTotalLeaseCount, possibleLeaseCount);
            }

            ValueStopwatch sw = ValueStopwatch.StartNew();
            // try to acquire leases until we have no more to aquire or no more possible
            while (!base.Cancellation.IsCancellationRequested && leasesToAquire > 0 && possibleLeaseCount > 0)
            {
                //select new queues to acquire
                List<QueueId> expectedQueues = queueSelector.NextSelection(leasesToAquire, myQueues.Select(queue=>queue.QueueId).ToList());
                // build lease request from each queue
                LeaseRequest[] leaseRequests = expectedQueues
                    .Select(queue => new LeaseRequest(queue.ToString(), options.LeaseLength))
                    .ToArray();

                AcquireLeaseResult[] results = await leaseProvider.Acquire(options.LeaseCategory, leaseRequests);
                //add successfully acquired queue to myQueues list
                for (var i = 0; i < results.Length; i++)
                {
                    AcquireLeaseResult result = results[i];
                    switch (result.StatusCode)
                    {
                        case ResponseCode.OK:
                            {
                                myQueues.Add(new AcquiredQueue(leaseOrder++, expectedQueues[i], result.AcquiredLease));
                                break;
                            }
                        case ResponseCode.TransientFailure:
                            {
                                Logger.LogWarning(result.FailureException, "Failed to acquire lease {LeaseKey} due to transient error.", result.AcquiredLease.ResourceKey);
                                break;
                            }
                        // this is expected much of the time
                        case ResponseCode.LeaseNotAvailable:
                            {
                                if (Logger.IsEnabled(LogLevel.Debug))
                                {
                                    Logger.LogDebug(result.FailureException, "Failed to acquire lease {LeaseKey} due to {Reason}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                                }
                                break;
                            }
                        // An acquire call should not return this code, so log as error
                        case ResponseCode.InvalidToken:
                            {
                                Logger.LogError(result.FailureException, "Failed to aquire acquire {LeaseKey} unexpected invalid token.", result.AcquiredLease.ResourceKey);
                                break;
                            }
                        default:
                            {
                                Logger.LogError(result.FailureException, "Unexpected response to acquire request of lease {LeaseKey}.  StatusCode {StatusCode}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                                break;
                            }
                    }
                }
                possibleLeaseCount -= expectedQueues.Count;
                leasesToAquire = expectedTotalLeaseCount - myQueues.Count;
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug("Holding leased for {QueueCount} queues.  Trying to acquire {AquireQueueCount} queues to reach {TargetQueueCount} of a possible {PossibleLeaseCount} lease", myQueues.Count, leasesToAquire, expectedTotalLeaseCount, possibleLeaseCount);
                }
                if (sw.Elapsed > timeout)
                {
                    // blown our alotted time, try again next period
                    break;
                }
            }

            Logger.LogInformation("Holding leases for {QueueCount} of an expected {MinQueueCount} queues.", myQueues.Count, responsibility);
        }

        /// <summary>
        /// Renew leases
        /// </summary>
        /// <returns>bool - false if we failed to renew all leases</returns>
        private async Task<bool> RenewLeases()
        {
            bool allRenewed = true;
            if (base.Cancellation.IsCancellationRequested) return false;
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("RenewLeases. QueueCount: {QueueCount}", myQueues.Count);
            }
            if (myQueues.Count <= 0)
                return allRenewed;
            var results = await leaseProvider.Renew(options.LeaseCategory, myQueues.Select(queue => queue.AcquiredLease).ToArray());
            //update myQueues list with successfully renewed leases
            for (var i = 0; i < results.Length; i++)
            {
                AcquireLeaseResult result = results[i];
                switch (result.StatusCode)
                {
                    case ResponseCode.OK:
                        {
                            myQueues[i].AcquiredLease = result.AcquiredLease;
                            break;
                        }
                    case ResponseCode.TransientFailure:
                        {
                            myQueues.RemoveAt(i);
                            allRenewed = false;
                            Logger.LogWarning(result.FailureException, "Failed to renew lease {LeaseKey} due to transient error.", result.AcquiredLease.ResourceKey);
                            break;
                        }
                    // these can occure if lease has expired and/or someone else has taken it
                    case ResponseCode.InvalidToken:
                    case ResponseCode.LeaseNotAvailable:
                        {
                            myQueues.RemoveAt(i);
                            allRenewed = false;
                            Logger.LogWarning(result.FailureException, "Failed to renew lease {LeaseKey} due to {Reason}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                    default:
                        {
                            myQueues.RemoveAt(i);
                            allRenewed = false;
                            Logger.LogError(result.FailureException, "Unexpected response to renew of lease {LeaseKey}.  StatusCode {StatusCode}.", result.AcquiredLease.ResourceKey, result.StatusCode);
                            break;
                        }
                }
            }
            Logger.LogInformation("Renewed leases for {QueueCount} queues.", myQueues.Count);
            return allRenewed;
        }

        private Task NotifyOnChange(HashSet<QueueId> oldQueues)
        {
            if (base.Cancellation.IsCancellationRequested) return Task.CompletedTask;
            var newQueues = new HashSet<QueueId>(myQueues.Select(queue => queue.QueueId));
            //if queue changed, notify listeners
            return !oldQueues.SetEquals(newQueues)
                ? NotifyListeners()
                : Task.CompletedTask;
        }

        /// <inheritdoc/>
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
                await executor.AddNext(() => UpdateResponsibilities(activeSilos));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Updating Responsibilities");
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
            responsibility = allQueuesCount / activeSiloCount;
            var overflow = allQueuesCount % activeSiloCount;
            if(overflow != 0 && AmGreedy(overflow, activeSilos))
            {
                responsibility++;
            }

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("Updating Responsibilities for {QueueCount} queue over {SiloCount} silos. Need {MinQueueCount} queues, have {MyQueueCount}",
                    allQueuesCount, activeSiloCount, responsibility, myQueues.Count);
            }

            if (myQueues.Count < responsibility && leaseAquisitionTimer == null)
            {
                leaseAquisitionTimer = timerRegistry.RegisterTimer(
                    null,
                    AcquireLeasesToMeetResponsibility,
                    null,
                    options.LeaseAquisitionPeriod,
                    options.LeaseAquisitionPeriod);
            }

            if (leaseMaintenanceTimer == null)
            {
                leaseMaintenanceTimer = timerRegistry.RegisterTimer(
                    null,
                    MaintainLeases,
                    null,
                    options.LeaseRenewPeriod,
                    options.LeaseRenewPeriod);
            }

            await AcquireLeasesToMeetResponsibility();
        }
    }
}
