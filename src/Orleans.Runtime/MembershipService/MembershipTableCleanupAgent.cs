#nullable enable
using System;
using Orleans.Configuration;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for cleaning up dead membership table entries.
    /// </summary>
    internal class MembershipTableCleanupAgent : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IMembershipTable _membershipTableProvider;
        private readonly ILogger<MembershipTableCleanupAgent> _logger;
        private readonly IAsyncTimer? _cleanupDefunctSilosTimer;

        public MembershipTableCleanupAgent(
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IMembershipTable membershipTableProvider,
            ILogger<MembershipTableCleanupAgent> log,
            IAsyncTimerFactory timerFactory)
        {
            _clusterMembershipOptions = clusterMembershipOptions.Value;
            _membershipTableProvider = membershipTableProvider;
            _logger = log;
            if (_clusterMembershipOptions.DefunctSiloCleanupPeriod.HasValue)
            {
                _cleanupDefunctSilosTimer = timerFactory.Create(
                    _clusterMembershipOptions.DefunctSiloCleanupPeriod.Value,
                    nameof(CleanupDefunctSilos));
            }
        }

        public void Dispose()
        {
            _cleanupDefunctSilosTimer?.Dispose();
        }

        private async Task CleanupDefunctSilos()
        {
            if (!_clusterMembershipOptions.DefunctSiloCleanupPeriod.HasValue)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug($"Membership table cleanup is disabled due to {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.DefunctSiloCleanupPeriod)} not being specified");
                }

                return;
            }

            Debug.Assert(_cleanupDefunctSilosTimer is not null);
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Starting membership table cleanup agent");
            try
            {
                var period = _clusterMembershipOptions.DefunctSiloCleanupPeriod.Value;

                // The first cleanup should be scheduled for shortly after silo startup.
                var delay = RandomTimeSpan.Next(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
                while (await _cleanupDefunctSilosTimer.NextTick(delay))
                {
                    // Select a random time within the next window.
                    // The purpose of this is to add jitter to a process which could be affected by contention with other silos.
                    delay = RandomTimeSpan.Next(period, period + TimeSpan.FromMinutes(5));
                    try
                    {
                        var dateLimit = DateTime.UtcNow - _clusterMembershipOptions.DefunctSiloExpiration;
                        await _membershipTableProvider.CleanupDefunctSiloEntries(dateLimit);
                    }
                    catch (Exception exception) when (exception is NotImplementedException or MissingMethodException)
                    {
                        _cleanupDefunctSilosTimer.Dispose();
                        _logger.LogWarning(
                            (int)ErrorCode.MembershipCleanDeadEntriesFailure,
                            $"{nameof(IMembershipTable.CleanupDefunctSiloEntries)} operation is not supported by the current implementation of {nameof(IMembershipTable)}. Disabling the timer now.");
                        return;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError((int)ErrorCode.MembershipCleanDeadEntriesFailure, exception, "Failed to clean up defunct membership table entries");
                    }
                }
            }
            finally
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Stopped membership table cleanup agent");
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            Task? task = null;
            lifecycle.Subscribe(nameof(MembershipTableCleanupAgent), ServiceLifecycleStage.Active, OnStart, OnStop);

            Task OnStart(CancellationToken ct)
            {
                task = Task.Run(CleanupDefunctSilos);
                return Task.CompletedTask;
            }

            async Task OnStop(CancellationToken ct)
            {
                _cleanupDefunctSilosTimer?.Dispose();
                if (task is { })
                {
                    await task.WaitAsync(ct).SuppressThrowing();
                }
            }
        }

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason)
        {
            if (_cleanupDefunctSilosTimer is IAsyncTimer timer)
            {
                return timer.CheckHealth(lastCheckTime, out reason);
            }

            reason = default;
            return true;
        }
    }
}
