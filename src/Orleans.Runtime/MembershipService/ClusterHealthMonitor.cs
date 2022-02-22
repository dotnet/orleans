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
using Orleans.Internal;
using static Orleans.Runtime.MembershipService.SiloHealthMonitor;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for ensuring that this silo monitors other silos in the cluster.
    /// </summary>
    internal class ClusterHealthMonitor : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, ClusterHealthMonitor.ITestAccessor
    {
        private readonly CancellationTokenSource shutdownCancellation = new CancellationTokenSource();
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IServiceProvider serviceProvider;
        private readonly MembershipTableManager membershipService;
        private readonly ILogger<ClusterHealthMonitor> log;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IOptionsMonitor<ClusterMembershipOptions> clusterMembershipOptions;
        private ImmutableDictionary<SiloAddress, SiloHealthMonitor> monitoredSilos = ImmutableDictionary<SiloAddress, SiloHealthMonitor>.Empty;
        private MembershipVersion observedMembershipVersion;
        private Func<SiloAddress, SiloHealthMonitor> createMonitor;
        private Func<SiloHealthMonitor, ProbeResult, Task> onProbeResult;

        /// <summary>
        /// Exposes private members of <see cref="ClusterHealthMonitor"/> for test purposes.
        /// </summary>
        internal interface ITestAccessor
        {
            ImmutableDictionary<SiloAddress, SiloHealthMonitor> MonitoredSilos { get; set; }
            Func<SiloAddress, SiloHealthMonitor> CreateMonitor { get; set; }
            MembershipVersion ObservedVersion { get; }
            Func<SiloHealthMonitor, ProbeResult, Task> OnProbeResult { get; set; }
        }

        public ClusterHealthMonitor(
            ILocalSiloDetails localSiloDetails,
            MembershipTableManager membershipService,
            ILogger<ClusterHealthMonitor> log,
            IOptionsMonitor<ClusterMembershipOptions> clusterMembershipOptions,
            IFatalErrorHandler fatalErrorHandler,
            IServiceProvider serviceProvider)
        {
            this.localSiloDetails = localSiloDetails;
            this.serviceProvider = serviceProvider;
            this.membershipService = membershipService;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
            this.clusterMembershipOptions = clusterMembershipOptions;
            this.onProbeResult = this.OnProbeResultInternal;
            Func<SiloHealthMonitor, ProbeResult, Task> onProbeResultFunc = (siloHealthMonitor, probeResult) => this.onProbeResult(siloHealthMonitor, probeResult);
            this.createMonitor = silo => ActivatorUtilities.CreateInstance<SiloHealthMonitor>(serviceProvider, silo, onProbeResultFunc);
        }

        ImmutableDictionary<SiloAddress, SiloHealthMonitor> ITestAccessor.MonitoredSilos { get => this.monitoredSilos; set => this.monitoredSilos = value; }
        Func<SiloAddress, SiloHealthMonitor> ITestAccessor.CreateMonitor { get => this.createMonitor; set => this.createMonitor = value; }
        MembershipVersion ITestAccessor.ObservedVersion => this.observedMembershipVersion;
        Func<SiloHealthMonitor, ProbeResult, Task> ITestAccessor.OnProbeResult { get => this.onProbeResult; set => this.onProbeResult = value; }

        /// <summary>
        /// Gets the collection of monitored silos.
        /// </summary>
        public ImmutableDictionary<SiloAddress, SiloHealthMonitor> SiloMonitors => this.monitoredSilos;

        private async Task ProcessMembershipUpdates()
        {
            try
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
                await foreach (var tableSnapshot in this.membershipService.MembershipTableUpdates.WithCancellation(this.shutdownCancellation.Token))
                {
                    var newMonitoredSilos = this.UpdateMonitoredSilos(tableSnapshot, this.monitoredSilos, DateTime.UtcNow);

                    foreach (var pair in this.monitoredSilos)
                    {
                        if (!newMonitoredSilos.ContainsKey(pair.Key))
                        {
                            var cancellation = new CancellationTokenSource(this.clusterMembershipOptions.CurrentValue.ProbeTimeout).Token;
                            await pair.Value.StopAsync(cancellation);
                        }
                    }

                    this.monitoredSilos = newMonitoredSilos;
                    this.observedMembershipVersion = tableSnapshot.Version;
                }
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopped processing membership updates");
            }
        }

        [Pure]
        private ImmutableDictionary<SiloAddress, SiloHealthMonitor> UpdateMonitoredSilos(
            MembershipTableSnapshot membership,
            ImmutableDictionary<SiloAddress, SiloHealthMonitor> monitoredSilos,
            DateTime now)
        {
            // If I am still not fully functional, I should not be probing others.
            if (!membership.Entries.TryGetValue(this.localSiloDetails.SiloAddress, out var self) || !IsFunctionalForMembership(self.Status))
            {
                return ImmutableDictionary<SiloAddress, SiloHealthMonitor>.Empty;
            }

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
                throw new OrleansMissingMembershipEntryException(error);
            }

            // Go over every node excluding me,
            // Find up to NumProbedSilos silos after me, which are not suspected by anyone and add them to the probedSilos,
            // In addition, every suspected silo you encounter on the way, add it to the probedSilos.
            var silosToWatch = new List<SiloAddress>();
            var additionalSilos = new List<SiloAddress>();

            for (int i = 0; i < tmpList.Count - 1 && silosToWatch.Count < this.clusterMembershipOptions.CurrentValue.NumProbedSilos; i++)
            {
                var candidate = tmpList[(myIndex + i + 1) % tmpList.Count];
                var candidateEntry = membership.Entries[candidate];

                if (candidate.IsSameLogicalSilo(this.localSiloDetails.SiloAddress)) continue;

                bool isSuspected = candidateEntry.GetFreshVotes(now, this.clusterMembershipOptions.CurrentValue.DeathVoteExpirationTimeout).Count > 0;
                if (isSuspected)
                {
                    additionalSilos.Add(candidate);
                }
                else
                {
                    silosToWatch.Add(candidate);
                }
            }

            // Take new watched silos, but leave the probe counters for the old ones.
            var newProbedSilos = ImmutableDictionary.CreateBuilder<SiloAddress, SiloHealthMonitor>();
            foreach (var silo in silosToWatch.Union(additionalSilos))
            {
                SiloHealthMonitor monitor;
                if (!monitoredSilos.TryGetValue(silo, out monitor))
                {
                    monitor = this.createMonitor(silo);
                    monitor.Start();
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

            static bool AreTheSame<T>(ImmutableDictionary<SiloAddress, T> first, ImmutableDictionary<SiloAddress, T> second)
            {
                return first.Count == second.Count && first.Count == first.Keys.Intersect(second.Keys).Count();
            }

            static bool IsFunctionalForMembership(SiloStatus status)
            {
                return status == SiloStatus.Active || status == SiloStatus.ShuttingDown || status == SiloStatus.Stopping;
            }
        }
        
        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();

            lifecycle.Subscribe(nameof(ClusterHealthMonitor), ServiceLifecycleStage.Active, OnActiveStart, OnActiveStop);

            Task OnActiveStart(CancellationToken ct)
            {
                tasks.Add(Task.Run(() => this.ProcessMembershipUpdates()));
                return Task.CompletedTask;
            }

            async Task OnActiveStop(CancellationToken ct)
            {
                this.shutdownCancellation.Cancel(throwOnFirstException: false);

                foreach (var monitor in this.monitoredSilos.Values)
                {
                    tasks.Add(monitor.StopAsync(ct));
                }

                this.monitoredSilos = ImmutableDictionary<SiloAddress, SiloHealthMonitor>.Empty;

                // Allow some minimum time for graceful shutdown.
                var shutdownGracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod), ct.WhenCancelled());
                await Task.WhenAny(shutdownGracePeriod, Task.WhenAll(tasks));
            }
        }

        /// <summary>
        /// Performs the default action when a new probe result is created.
        /// </summary>
        private async Task OnProbeResultInternal(SiloHealthMonitor monitor, ProbeResult probeResult)
        {
            // Do not act on probe results if shutdown is in progress.
            if (this.shutdownCancellation.IsCancellationRequested)
            {
                return;
            }

            if (probeResult.IsDirectProbe)
            {
                if (probeResult.Status == ProbeResultStatus.Failed && probeResult.FailedProbeCount >= this.clusterMembershipOptions.CurrentValue.NumMissedProbesLimit)
                {
                    await this.membershipService.TryToSuspectOrKill(monitor.SiloAddress).ConfigureAwait(false);
                }
            }
            else if (probeResult.Status == ProbeResultStatus.Failed)
            {
                if (this.clusterMembershipOptions.CurrentValue.NumVotesForDeathDeclaration <= 2)
                {
                    // Since both this silo and another silo were unable to probe the target silo, we declare it dead.
                    await this.membershipService.TryKill(monitor.SiloAddress).ConfigureAwait(false);
                }
                else
                {
                    await this.membershipService.TryToSuspectOrKill(monitor.SiloAddress).ConfigureAwait(false);
                }
            }
        }

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, out string reason)
        {
            var ok = true;
            reason = default;
            foreach (var monitor in this.monitoredSilos.Values)
            {
                ok &= monitor.CheckHealth(lastCheckTime, out var monitorReason);
                if (!string.IsNullOrWhiteSpace(monitorReason))
                {
                    var siloReason = $"Monitor for {monitor.SiloAddress} is degraded with: {monitorReason}.";
                    if (string.IsNullOrWhiteSpace(reason))
                    {
                        reason = siloReason;
                    }
                    else
                    {
                        reason = reason + " " + siloReason;
                    }
                }
            }

            return ok;
        }
    }
}
