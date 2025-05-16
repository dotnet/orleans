using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
    internal partial class ClusterHealthMonitor : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, ClusterHealthMonitor.ITestAccessor, IDisposable, IAsyncDisposable
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
                LogDebugStartingToProcessMembershipUpdates(log);
                await foreach (var tableSnapshot in this.membershipService.MembershipTableUpdates.WithCancellation(this.shutdownCancellation.Token))
                {
                    var utcNow = DateTime.UtcNow;

                    var newMonitoredSilos = this.UpdateMonitoredSilos(tableSnapshot, this.monitoredSilos, utcNow);

                    if (this.clusterMembershipOptions.CurrentValue.EvictWhenMaxJoinAttemptTimeExceeded)
                    {
                        await this.EvictStaleStateSilos(tableSnapshot, utcNow);
                    }

                    foreach (var pair in this.monitoredSilos)
                    {
                        if (!newMonitoredSilos.ContainsKey(pair.Key))
                        {
                            using var cancellation = new CancellationTokenSource(this.clusterMembershipOptions.CurrentValue.ProbeTimeout);
                            await pair.Value.StopAsync(cancellation.Token);
                        }
                    }

                    this.monitoredSilos = newMonitoredSilos;
                    this.observedMembershipVersion = tableSnapshot.Version;
                }
            }
            catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
            {
                // Ignore and continue shutting down.
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                LogDebugStoppedProcessingMembershipUpdates(log);
            }
        }

        private async Task EvictStaleStateSilos(
            MembershipTableSnapshot membership,
            DateTime utcNow)
        {
            foreach (var member in membership.Entries)
            {
                if (IsCreatedOrJoining(member.Value.Status)
                    && HasExceededMaxJoinTime(
                        startTime: member.Value.StartTime,
                        now: utcNow,
                        maxJoinTime: this.clusterMembershipOptions.CurrentValue.MaxJoinAttemptTime))
                {
                    try
                    {
                        LogDebugStaleSiloFound(log);
                        await this.membershipService.TryToSuspectOrKill(member.Key);
                    }
                    catch(Exception exception)
                    {
                        LogErrorTryToSuspectOrKillFailed(log, exception, member.Value.SiloAddress, member.Value.Status);
                    }
                }
            }

            static bool IsCreatedOrJoining(SiloStatus status)
            {
                return status == SiloStatus.Created || status == SiloStatus.Joining;
            }

            static bool HasExceededMaxJoinTime(DateTime startTime, DateTime now, TimeSpan maxJoinTime)
            {
                return now > startTime.Add(maxJoinTime);
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

            var options = clusterMembershipOptions.CurrentValue;
            var numProbedSilos = options.NumProbedSilos;

            // Go over every node excluding this one.
            // Find up to NumProbedSilos silos after me, which are not suspected by anyone and add them to the probedSilos,
            // In addition, every suspected silo you encounter on the way, add it to the probedSilos.
            var silosToWatch = new List<SiloAddress>();
            var additionalSilos = new List<SiloAddress>();

            var tmpList = new List<(SiloAddress SiloAddress, int HashCode)>();
            foreach (var (candidate, entry) in membership.Entries)
            {
                // Watch shutting-down silos as well, so we can properly ensure they become dead.
                if (!IsFunctionalForMembership(entry.Status))
                {
                    continue;
                }

                tmpList.Add((candidate, 0));

                // Ignore the local silo.
                if (candidate.IsSameLogicalSilo(this.localSiloDetails.SiloAddress))
                {
                    continue;
                }

                // Monitor all suspected and stale silos.
                if (entry.GetFreshVotes(now, options.DeathVoteExpirationTimeout).Count > 0
                    || entry.HasMissedIAmAlives(options, now))
                {
                    additionalSilos.Add(candidate);
                }
            }

            // Each silo monitors up to NumProbedSilos other silos.
            // Monitoring is determined using multiple hash rings, each generated with a different seeded hash function.
            // For each hash ring:
            // 1. The hash values of all silos are updated based on the ring's seed and sorted to determine their positions.
            // 2. The local silo finds its position in the sorted list and iterates over subsequent silos, wrapping around at the ends.
            // 3. The first silo not already being monitored is selected and added to the monitoring set.
            //
            // This approach probabilistically constructs an Expander Graph (https://en.wikipedia.org/wiki/Expander_graph).
            // Expander graphs improve fault detection time when there are multiple concurrent failures by minimizing overlap
            // in monitoring sets between any two silos and reducing dependency chains (e.g., avoiding cases where one failed
            // silo must be evicted before another failed silo can be detected).
            // The idea to use an expander graph is taken from "Stable and Consistent Membership at Scale with Rapid" by Lalith Suresh et al:
            // https://www.usenix.org/conference/atc18/presentation/suresh
            for (var ringNum = 0; ringNum < numProbedSilos; ++ringNum)
            {
                // Update hash values with the current ring number.
                for (var i = 0; i < tmpList.Count; i++)
                {
                    var siloAddress = tmpList[i].SiloAddress;
                    tmpList[i] = (siloAddress, siloAddress.GetConsistentHashCode(ringNum));
                }

                // Sort the candidates based on their updated hash values.
                tmpList.Sort((x, y) => x.HashCode.CompareTo(y.HashCode));

                var myIndex = tmpList.FindIndex(el => el.SiloAddress.Equals(self.SiloAddress));
                if (myIndex < 0)
                {
                    LogErrorSiloNotInLocalList(log, self.SiloAddress, self.Status);
                    throw new OrleansMissingMembershipEntryException(
                        $"This silo {self.SiloAddress} status {self.Status} is not in its own local silo list! This is a bug!");
                }

                // Starting at the local silo's index, find the first non-monitored silo and add it to the list.
                for (var i = 0; i < tmpList.Count - 1; i++)
                {
                    var candidate = tmpList[(myIndex + i + 1) % tmpList.Count].SiloAddress;
                    if (!silosToWatch.Contains(candidate))
                    {
                        Debug.Assert(!candidate.IsSameLogicalSilo(this.localSiloDetails.SiloAddress));
                        silosToWatch.Add(candidate);
                        break;
                    }
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
                LogInformationWillWatchActivelyPing(log, newProbedSilos.Count, new(newProbedSilos.Keys));
            }

            return result;

            static bool AreTheSame<T>(ImmutableDictionary<SiloAddress, T> first, ImmutableDictionary<SiloAddress, T> second)
                => first.Count == second.Count && first.Count == first.Keys.Intersect(second.Keys).Count();

            static bool IsFunctionalForMembership(SiloStatus status)
                => status is SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping;
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
                    await this.membershipService.TryToSuspectOrKill(monitor.TargetSiloAddress).ConfigureAwait(false);
                }
            }
            else if (probeResult.Status == ProbeResultStatus.Failed)
            {
                await this.membershipService.TryToSuspectOrKill(monitor.TargetSiloAddress, probeResult.Intermediary).ConfigureAwait(false);
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
                    var siloReason = $"Monitor for {monitor.TargetSiloAddress} is degraded with: {monitorReason}.";
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

        public void Dispose()
        {
            try
            {
                shutdownCancellation.Cancel();
            }
            catch (Exception exception)
            {
                LogErrorCancellingShutdownToken(log, exception);
            }

            foreach (var monitor in monitoredSilos.Values)
            {
                try
                {
                    monitor.Dispose();
                }
                catch (Exception exception)
                {
                    LogErrorDisposingMonitorForSilo(log, exception, monitor.TargetSiloAddress);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                shutdownCancellation.Cancel();
            }
            catch (Exception exception)
            {
                LogErrorCancellingShutdownToken(log, exception);
            }

            var tasks = new List<Task>();
            foreach (var monitor in monitoredSilos.Values)
            {
                try
                {
                    tasks.Add(monitor.DisposeAsync().AsTask());
                }
                catch (Exception exception)
                {
                    LogErrorDisposingMonitorForSilo(log, exception, monitor.TargetSiloAddress);
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // --- Logging methods ---

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting to process membership updates"
        )]
        private static partial void LogDebugStartingToProcessMembershipUpdates(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Stopped processing membership updates"
        )]
        private static partial void LogDebugStoppedProcessingMembershipUpdates(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Stale silo with a joining or created state found, calling 'TryToSuspectOrKill'"
        )]
        private static partial void LogDebugStaleSiloFound(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Silo {SuspectAddress} has had the status '{SiloStatus}' for longer than 'MaxJoinAttemptTime' but a call to 'TryToSuspectOrKill' has failed"
        )]
        private static partial void LogErrorTryToSuspectOrKillFailed(ILogger logger, Exception exception, SiloAddress suspectAddress, SiloStatus siloStatus);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100305,
            Level = LogLevel.Error,
            Message = "This silo {SiloAddress} status {Status} is not in its own local silo list! This is a bug!"
        )]
        private static partial void LogErrorSiloNotInLocalList(ILogger logger, SiloAddress siloAddress, SiloStatus status);

        private readonly struct ProbedSilosLogRecord(IEnumerable<SiloAddress> probedSilos)
        {
            public override string ToString() => Utils.EnumerableToString(probedSilos);
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.MembershipWatchList,
            Level = LogLevel.Information,
            Message = "Will watch (actively ping) {ProbedSiloCount} silos: {ProbedSilos}"
        )]
        private static partial void LogInformationWillWatchActivelyPing(ILogger logger, int probedSiloCount, ProbedSilosLogRecord probedSilos);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error cancelling shutdown token."
        )]
        private static partial void LogErrorCancellingShutdownToken(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error disposing monitor for {SiloAddress}."
        )]
        private static partial void LogErrorDisposingMonitorForSilo(ILogger logger, Exception exception, SiloAddress siloAddress);
    }
}
