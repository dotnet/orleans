using System;
using System.Collections.Generic;
using Orleans.Configuration;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Internal;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for cleaning up dead membership table entries.
    /// </summary>
    internal class MembershipTableCleanupAgent : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly IMembershipTable membershipTableProvider;
        private readonly ILogger<MembershipTableCleanupAgent> log;
        private readonly IAsyncTimer cleanupDefunctSilosTimer;

        public MembershipTableCleanupAgent(
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IMembershipTable membershipTableProvider,
            ILogger<MembershipTableCleanupAgent> log,
            IAsyncTimerFactory timerFactory)
        {
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
            this.membershipTableProvider = membershipTableProvider;
            this.log = log;
            if (this.clusterMembershipOptions.DefunctSiloCleanupPeriod.HasValue)
            {
                cleanupDefunctSilosTimer = timerFactory.Create(
                    this.clusterMembershipOptions.DefunctSiloCleanupPeriod.Value,
                    nameof(CleanupDefunctSilos));
            }
        }

        public void Dispose() => cleanupDefunctSilosTimer?.Dispose();

        private async Task CleanupDefunctSilos()
        {
            if (!clusterMembershipOptions.DefunctSiloCleanupPeriod.HasValue)
            {
                if (log.IsEnabled(LogLevel.Debug))
                {
                    log.LogDebug($"Membership table cleanup is disabled due to {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.DefunctSiloCleanupPeriod)} not being specified");
                }

                return;
            }

            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Starting membership table cleanup agent");
            try
            {
                var period = clusterMembershipOptions.DefunctSiloCleanupPeriod.Value;

                // The first cleanup should be scheduled for shortly after silo startup.
                var delay = RandomTimeSpan.Next(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
                while (await cleanupDefunctSilosTimer.NextTick(delay))
                {
                    // Select a random time within the next window.
                    // The purpose of this is to add jitter to a process which could be affected by contention with other silos.
                    delay = RandomTimeSpan.Next(period, period + TimeSpan.FromMinutes(5));
                    try
                    {
                        var dateLimit = DateTime.UtcNow - clusterMembershipOptions.DefunctSiloExpiration;
                        await membershipTableProvider.CleanupDefunctSiloEntries(dateLimit);
                    }
                    catch (Exception exception) when (exception is NotImplementedException or MissingMethodException)
                    {
                        cleanupDefunctSilosTimer.Dispose();
                        log.LogWarning(
                            (int)ErrorCode.MembershipCleanDeadEntriesFailure,
                            $"{nameof(IMembershipTable.CleanupDefunctSiloEntries)} operation is not supported by the current implementation of {nameof(IMembershipTable)}. Disabling the timer now.");
                        return;
                    }
                    catch (Exception exception)
                    {
                        log.LogError((int)ErrorCode.MembershipCleanDeadEntriesFailure, exception, "Failed to clean up defunct membership table entries");
                    }
                }
            }
            finally
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Stopped membership table cleanup agent");
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();
            lifecycle.Subscribe(nameof(MembershipTableCleanupAgent), ServiceLifecycleStage.Active, OnStart, OnStop);

            Task OnStart(CancellationToken ct)
            {
                tasks.Add(Task.Run(() => CleanupDefunctSilos()));
                return Task.CompletedTask;
            }

            async Task OnStop(CancellationToken ct)
            {
                cleanupDefunctSilosTimer?.Dispose();
                await Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(tasks));
            }
        }

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, out string reason)
        {
            if (cleanupDefunctSilosTimer is IAsyncTimer timer)
            {
                return timer.CheckHealth(lastCheckTime, out reason);
            }

            reason = default;
            return true;
        }
    }
}
