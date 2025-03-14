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
    internal partial class MembershipTableCleanupAgent : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
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
                LogDebugMembershipTableCleanupDisabled(_logger);
                return;
            }

            Debug.Assert(_cleanupDefunctSilosTimer is not null);
            LogDebugStartingMembershipTableCleanupAgent(_logger);
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
                        LogWarningCleanupDefunctSiloEntriesNotSupported(_logger);
                        return;
                    }
                    catch (Exception exception)
                    {
                        LogErrorFailedToCleanUpDefunctMembershipTableEntries(_logger, exception);
                    }
                }
            }
            finally
            {
                LogDebugStoppedMembershipTableCleanupAgent(_logger);
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

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Membership table cleanup is disabled due to ClusterMembershipOptions.DefunctSiloCleanupPeriod not being specified"
        )]
        private static partial void LogDebugMembershipTableCleanupDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting membership table cleanup agent"
        )]
        private static partial void LogDebugStartingMembershipTableCleanupAgent(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "IMembershipTable.CleanupDefunctSiloEntries operation is not supported by the current implementation of IMembershipTable. Disabling the timer now."
        )]
        private static partial void LogWarningCleanupDefunctSiloEntriesNotSupported(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to clean up defunct membership table entries"
        )]
        private static partial void LogErrorFailedToCleanUpDefunctMembershipTableEntries(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Stopped membership table cleanup agent"
        )]
        private static partial void LogDebugStoppedMembershipTableCleanupAgent(ILogger logger);
    }
}
