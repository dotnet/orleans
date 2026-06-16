using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for cleaning up dead membership table entries.
    /// </summary>
    internal partial class MembershipTableCleanupAgent : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IMembershipTable _membershipTableProvider;
        private readonly IMembershipManager _membershipManager;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<MembershipTableCleanupAgent> _logger;
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly object _shutdownLock = new();
        private bool _disposed;
        private bool _cleanupDefunctSiloEntriesUnsupported;

        public MembershipTableCleanupAgent(
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IMembershipTable membershipTableProvider,
            IMembershipManager membershipManager,
            ILocalSiloDetails localSiloDetails,
            TimeProvider timeProvider,
            ILogger<MembershipTableCleanupAgent> log)
        {
            _clusterMembershipOptions = clusterMembershipOptions.Value;
            _membershipTableProvider = membershipTableProvider;
            _membershipManager = membershipManager;
            _localSiloDetails = localSiloDetails;
            _timeProvider = timeProvider;
            _logger = log;
        }

        public void Dispose()
        {
            lock (_shutdownLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _shutdownCts.Cancel();
                _shutdownCts.Dispose();
            }
        }

        private void SignalShutdown()
        {
            lock (_shutdownLock)
            {
                if (!_disposed)
                {
                    _shutdownCts.Cancel();
                }
            }
        }

        private async Task ProcessMembershipUpdates(CancellationToken cancellationToken)
        {
            if (!_clusterMembershipOptions.DefunctSiloCleanupPeriod.HasValue
                && !_clusterMembershipOptions.MaxDefunctSiloEntries.HasValue)
            {
                LogDebugMembershipTableCleanupDisabled(_logger);
                return;
            }

            LogDebugStartingMembershipTableCleanupAgent(_logger);
            try
            {
                await foreach (var membership in _membershipManager.MembershipUpdates.WithCancellation(cancellationToken))
                {
                    if (_cleanupDefunctSiloEntriesUnsupported)
                    {
                        return;
                    }

                    if (!IsFirstActiveSilo(membership))
                    {
                        continue;
                    }

                    await CleanupDefunctSilos(membership, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ignore and continue shutting down.
            }
            finally
            {
                LogDebugStoppedMembershipTableCleanupAgent(_logger);
            }
        }

        private async Task CleanupDefunctSilos(MembershipTableSnapshot membership, CancellationToken cancellationToken)
        {
            try
            {
                DateTimeOffset? beforeDate = default;

                if (_clusterMembershipOptions.DefunctSiloCleanupPeriod.HasValue)
                {
                    beforeDate = _timeProvider.GetUtcNow() - _clusterMembershipOptions.DefunctSiloExpiration;
                }

                if (_clusterMembershipOptions.MaxDefunctSiloEntries is { } maxDefunctSiloEntries)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(maxDefunctSiloEntries, nameof(ClusterMembershipOptions.MaxDefunctSiloEntries));

                    var defunctSiloEntryCount = 0;
                    var trackedEntryCount = (long)maxDefunctSiloEntries + 1;
                    var newestDefunctEntries = new PriorityQueue<MembershipEntry, DefunctSiloEntryPriority>();
                    foreach (var entry in membership.Entries.Values)
                    {
                        if (entry.Status != SiloStatus.Dead)
                        {
                            continue;
                        }

                        defunctSiloEntryCount++;
                        if (newestDefunctEntries.Count < trackedEntryCount)
                        {
                            newestDefunctEntries.Enqueue(entry, new DefunctSiloEntryPriority(entry));
                        }
                        else if (newestDefunctEntries.TryPeek(out var oldestTrackedEntry, out _)
                            && CompareDefunctSiloEntries(entry, oldestTrackedEntry) > 0)
                        {
                            newestDefunctEntries.Dequeue();
                            newestDefunctEntries.Enqueue(entry, new DefunctSiloEntryPriority(entry));
                        }
                    }

                    if (defunctSiloEntryCount > maxDefunctSiloEntries)
                    {
                        var newestEntryToRemove = newestDefunctEntries.Peek();
                        var excessBeforeDate = GetDefunctSiloCleanupCutoff(newestEntryToRemove.EffectiveIAmAliveTime);
                        if (!beforeDate.HasValue || excessBeforeDate > beforeDate.Value)
                        {
                            beforeDate = excessBeforeDate;
                        }
                    }
                }

                if (!beforeDate.HasValue)
                {
                    return;
                }

                LogDebugCleaningUpDefunctMembershipTableEntries(_logger, beforeDate.Value);
                await _membershipTableProvider.CleanupDefunctSiloEntries(beforeDate.Value).WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is NotImplementedException or MissingMethodException)
            {
                _cleanupDefunctSiloEntriesUnsupported = true;
                LogWarningCleanupDefunctSiloEntriesNotSupported(_logger);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogErrorFailedToCleanUpDefunctMembershipTableEntries(_logger, exception);
            }
        }

        private bool IsFirstActiveSilo(MembershipTableSnapshot membership)
        {
            var localSiloIsActive = false;
            foreach (var entry in membership.Entries.Values)
            {
                if (entry.Status != SiloStatus.Active)
                {
                    continue;
                }

                var comparison = entry.SiloAddress.CompareTo(_localSiloDetails.SiloAddress);
                if (comparison < 0)
                {
                    return false;
                }

                if (comparison == 0)
                {
                    localSiloIsActive = true;
                }
            }

            return localSiloIsActive;
        }

        private static int CompareDefunctSiloEntries(MembershipEntry left, MembershipEntry right)
        {
            var result = left.EffectiveIAmAliveTime.CompareTo(right.EffectiveIAmAliveTime);
            return result != 0 ? result : left.SiloAddress.CompareTo(right.SiloAddress);
        }

        private static DateTimeOffset GetDefunctSiloCleanupCutoff(DateTime effectiveIAmAliveTime)
        {
            var effectiveIAmAliveTimeUtc = DateTime.SpecifyKind(effectiveIAmAliveTime, DateTimeKind.Utc);
            return effectiveIAmAliveTimeUtc == DateTime.MaxValue
                ? DateTimeOffset.MaxValue
                : new DateTimeOffset(effectiveIAmAliveTimeUtc.AddTicks(1));
        }

        private readonly struct DefunctSiloEntryPriority(MembershipEntry entry) : IComparable<DefunctSiloEntryPriority>
        {
            private readonly MembershipEntry _entry = entry;

            public int CompareTo(DefunctSiloEntryPriority other) => CompareDefunctSiloEntries(_entry, other._entry);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            Task? task = null;
            lifecycle.Subscribe(nameof(MembershipTableCleanupAgent), ServiceLifecycleStage.Active, OnStart, OnStop);

            Task OnStart(CancellationToken ct)
            {
                task = Task.Run(() => ProcessMembershipUpdates(_shutdownCts.Token));
                return Task.CompletedTask;
            }

            async Task OnStop(CancellationToken ct)
            {
                SignalShutdown();
                if (task is { })
                {
                    await task.WaitAsync(ct).SuppressThrowing();
                }
            }
        }

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason)
        {
            reason = default;
            return true;
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Membership table cleanup is disabled due to ClusterMembershipOptions.DefunctSiloCleanupPeriod and ClusterMembershipOptions.MaxDefunctSiloEntries not being specified"
        )]
        private static partial void LogDebugMembershipTableCleanupDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting membership table cleanup agent"
        )]
        private static partial void LogDebugStartingMembershipTableCleanupAgent(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Cleaning up defunct membership table entries older than {BeforeDate}"
        )]
        private static partial void LogDebugCleaningUpDefunctMembershipTableEntries(ILogger logger, DateTimeOffset beforeDate);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "IMembershipTable.CleanupDefunctSiloEntries operation is not supported by the current implementation of IMembershipTable. Disabling defunct membership table cleanup."
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
