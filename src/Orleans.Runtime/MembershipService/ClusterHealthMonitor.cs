using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for ensuring that this silo monitors other silos in the cluster.
    /// </summary>
    internal class ClusterHealthMonitor : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager tableManager;
        private readonly ILogger<ClusterHealthMonitor> log;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IServiceProvider serviceProvider;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly IAsyncTimer monitorClusterHealthTimer;
        private int probeNumber;

        public ClusterHealthMonitor(
            MembershipTableManager tableManager,
            ILogger<ClusterHealthMonitor> log,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IFatalErrorHandler fatalErrorHandler,
            IServiceProvider serviceProvider,
            IAsyncTimerFactory timerFactory)
        {
            this.tableManager = tableManager;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
            this.serviceProvider = serviceProvider;
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
            this.monitorClusterHealthTimer = timerFactory.Create(
                this.clusterMembershipOptions.ProbeTimeout,
                nameof(MonitorClusterHealth));
        }

        private ImmutableDictionary<SiloAddress, SiloHealthMonitor> MonitoredSilos { get; set; } = ImmutableDictionary<SiloAddress, SiloHealthMonitor>.Empty;

        /// <summary>
        /// Attempts to probe all active silos in the cluster, returning a list of silos with which connectivity could not be verified.
        /// </summary>
        /// <returns>A list of silos with which connectivity could not be verified.</returns>
        public async Task<List<SiloAddress>> CheckClusterConnectivity(SiloAddress[] members)
        {
            var tasks = new List<Task>(members.Length);

            this.log.LogInformation(
                (int)ErrorCode.MembershipSendingPreJoinPing,
                "About to send pings to {Count} nodes in order to validate communication in the Joining state. Pinged nodes = {Nodes}",
                members.Length,
                Utils.EnumerableToString(members));

            foreach (var silo in members)
            {
                tasks.Add(this.CreateMonitor(silo).Probe(Interlocked.Increment(ref this.probeNumber)));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Ignore exceptions for now.
            }

            var failed = new List<SiloAddress>();
            for (var i = 0; i < tasks.Count; i++)
            {
                if (tasks[i].Status != TaskStatus.RanToCompletion)
                {
                    failed.Add(members[i]);
                }
            }

            return failed;
        }

        private async Task ProcessMembershipUpdates()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();
            var current = this.tableManager.MembershipTableUpdates;

            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    if (current.HasValue)
                    {
                        this.MonitoredSilos = this.UpdateMonitoredSilos(current.Value, this.MonitoredSilos, DateTime.UtcNow);
                    }
                    
                    var next = current.NextAsync();

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (ReferenceEquals(task, cancellationTask)) break;

                    current = next.GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopped processing membership updates");
            }
        }

        private async Task MonitorClusterHealth()
        {
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting cluster health monitor");
            try
            {
                // Randomize the initial wait time before initiating probes.
                var random = new SafeRandom();
                TimeSpan? onceOffDelay = random.NextTimeSpan(this.clusterMembershipOptions.ProbeTimeout);

                while (await this.monitorClusterHealthTimer.NextTick(onceOffDelay))
                {
                    if (onceOffDelay != default) onceOffDelay = default;
                    _ = this.ProbeMonitoredSilos();
                }
            }
            catch (Exception exception)
            {
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopped cluster health monitor");
            }
        }

        private async Task ProbeMonitoredSilos()
        {
            try
            {
                var tasks = new List<Task>(this.MonitoredSilos.Count);
                foreach (var pair in this.MonitoredSilos)
                {
                    var monitor = pair.Value;
                    tasks.Add(PingSilo(monitor));
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception exception)
            {
                this.log.LogError(
                    (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                    "Exception while monitoring cluster members: {Exception}",
                    exception);
            }

            async Task PingSilo(SiloHealthMonitor monitor)
            {
                var failedProbes = await monitor.Probe(Interlocked.Increment(ref this.probeNumber));

                if (this.cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (failedProbes < this.clusterMembershipOptions.NumMissedProbesLimit)
                {
                    return;
                }

                if (!this.MonitoredSilos.ContainsKey(monitor.SiloAddress))
                {
                    this.log.LogInformation(
                        (int)ErrorCode.MembershipPingedSiloNotInWatchList,
                        "Ignoring probe failure from silo {Silo} since it is no longer being monitored.",
                        monitor.SiloAddress,
                        failedProbes);
                    return;
                }

                this.log.LogInformation("Silo {Silo} failed {FailedProbes} probes and is suspected of being dead. Publishing a death vote.", monitor.SiloAddress, failedProbes);

                try
                {
                    await this.tableManager.TryToSuspectOrKill(monitor.SiloAddress);
                }
                catch (Exception exception)
                {
                    this.log.LogError((int)ErrorCode.MembershipFailedToSuspect, "Failed to register death vote for silo {Silo}: {Exception}", monitor.SiloAddress, exception);
                }
            }
        }

        [Pure]
        private ImmutableDictionary<SiloAddress, SiloHealthMonitor> UpdateMonitoredSilos(
            MembershipTableSnapshot membership,
            ImmutableDictionary<SiloAddress, SiloHealthMonitor> monitoredSilos,
            DateTime now)
        {
            var localSiloAddress = membership.LocalSilo.SiloAddress;
            if (!membership.Entries.TryGetValue(localSiloAddress, out var self))
            {
                return ImmutableDictionary<SiloAddress, SiloHealthMonitor>.Empty;
            }

            // if I am still not fully functional, I should not be probing others.
            if (!IsFunctionalForMembership(self.Status)) return monitoredSilos;

            // keep watching shutting-down silos as well, so we can properly ensure they are dead.
            var tmpList = new List<SiloAddress>();
            foreach (var member in membership.Entries)
            {
                if (IsFunctionalForMembership(member.Value.Status))
                {
                    tmpList.Add(member.Key);
                }
            }

            tmpList.Sort((x, y) => x.GetConsistentHashCode().CompareTo(y.GetConsistentHashCode()));

            int myIndex = tmpList.FindIndex(el => el.Equals(self.SiloAddress));
            if (myIndex < 0)
            {
                // this should not happen ...
                var error = string.Format("This silo {0} status {1} is not in its own local silo list! This is a bug!", self.SiloAddress.ToLongString(), self.Status);
                log.Error(ErrorCode.Runtime_Error_100305, error);
                throw new Exception(error);
            }

            // Go over every node excluding me,
            // Find up to NumProbedSilos silos after me, which are not suspected by anyone and add them to the probedSilos,
            // In addition, every suspected silo you encounter on the way, add him to the probedSilos.
            var silosToWatch = new List<SiloAddress>();
            var additionalSilos = new List<SiloAddress>();

            for (int i = 0; i < tmpList.Count - 1 && silosToWatch.Count < this.clusterMembershipOptions.NumProbedSilos; i++)
            {
                var candidate = tmpList[(myIndex + i + 1) % tmpList.Count];
                var candidateEntry = membership.Entries[candidate];

                bool isSuspected = candidateEntry.GetFreshVotes(now, this.clusterMembershipOptions.DeathVoteExpirationTimeout).Count > 0;
                if (isSuspected)
                {
                    additionalSilos.Add(candidate);
                }
                else
                {
                    silosToWatch.Add(candidate);
                }
            }

            // take new watched silos, but leave the probe counters for the old ones.
            var newProbedSilos = ImmutableDictionary.CreateBuilder<SiloAddress, SiloHealthMonitor>();
            foreach (var silo in silosToWatch.Union(additionalSilos))
            {
                SiloHealthMonitor monitor;
                if (!monitoredSilos.TryGetValue(silo, out monitor))
                {
                    monitor = CreateMonitor(silo);
                }

                newProbedSilos[silo] = monitor;
            }

            var result = newProbedSilos.ToImmutable();
            if (!AreTheSame(monitoredSilos, result))
            {
                log.Info(ErrorCode.MembershipWatchList, "Will watch (actively ping) {0} silos: {1}",
                    newProbedSilos.Count, Utils.EnumerableToString(newProbedSilos.Keys, silo => silo.ToLongString()));
            }

            return result;

            bool AreTheSame<T>(ImmutableDictionary<SiloAddress, T> first, ImmutableDictionary<SiloAddress, T> second)
            {
                return first.Count == second.Count && first.Count == first.Keys.Intersect(second.Keys).Count();
            }

            bool IsFunctionalForMembership(SiloStatus status)
            {
                return status == SiloStatus.Active || status == SiloStatus.ShuttingDown || status == SiloStatus.Stopping;
            }
        }

        private SiloHealthMonitor CreateMonitor(SiloAddress silo)
        {
            return ActivatorUtilities.CreateInstance<SiloHealthMonitor>(this.serviceProvider, silo);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var becomeActiveTasks = new List<Task>();

            lifecycle.Subscribe(nameof(ClusterHealthMonitor), ServiceLifecycleStage.BecomeActive, OnBecomeActiveStart, OnBecomeActiveStop);

            Task OnBecomeActiveStart(CancellationToken ct)
            {
                becomeActiveTasks.Add(this.ProcessMembershipUpdates());
                becomeActiveTasks.Add(this.MonitorClusterHealth());
                return Task.CompletedTask;
            }

            Task OnBecomeActiveStop(CancellationToken ct)
            {
                this.monitorClusterHealthTimer.Dispose();
                this.cancellation.Cancel(throwOnFirstException: false);

                // Stop waiting for graceful shutdown when the provided cancellation token is cancelled
                return Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(becomeActiveTasks));
            }
        }
    }
}
