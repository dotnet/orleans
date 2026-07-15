using System;
using System.Collections.Generic;
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
    internal partial class MembershipTableCleanupAgent : ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly ClusterMembershipOptions _clusterMembershipOptions;
        private readonly IMembershipTable _membershipTableProvider;
        private readonly IMembershipManager _membershipManager;
        private readonly ILocalSiloDetails _localSiloDetails;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<MembershipTableCleanupAgent> _logger;
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly object _shutdownLock = new();
        private DateTimeOffset? _lastDefunctSiloCleanupTime;
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
                await foreach (var _ in _membershipManager.MembershipUpdates.WithCancellation(cancellationToken))
                {
                    if (_cleanupDefunctSiloEntriesUnsupported)
                    {
                        return;
                    }

                    var membership = _membershipManager.CurrentSnapshot;
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
                var now = _timeProvider.GetUtcNow();
                DateTimeOffset? beforeDate = default;

                if (_clusterMembershipOptions.DefunctSiloCleanupPeriod is { } cleanupPeriod)
                {
                    var expirationBeforeDate = now - _clusterMembershipOptions.DefunctSiloExpiration;
                    if (ShouldCleanupExpiredSilos(membership, now, expirationBeforeDate, cleanupPeriod))
                    {
                        beforeDate = expirationBeforeDate;
                    }
                }

                if (_clusterMembershipOptions.MaxDefunctSiloEntries is { } maxDefunctSiloEntries)
                {
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
                        var entryPriority = new DefunctSiloEntryPriority(entry);
                        if (newestDefunctEntries.Count < trackedEntryCount)
                        {
                            newestDefunctEntries.Enqueue(entry, entryPriority);
                        }
                        else if (newestDefunctEntries.TryPeek(out _, out var oldestTrackedEntryPriority)
                            && entryPriority > oldestTrackedEntryPriority)
                        {
                            newestDefunctEntries.Dequeue();
                            newestDefunctEntries.Enqueue(entry, entryPriority);
                        }
                    }

                    if (defunctSiloEntryCount > maxDefunctSiloEntries)
                    {
                        var newestEntryToRemove = newestDefunctEntries.Peek();
                        var excessBeforeDate = GetDefunctSiloCleanupCutoff(newestEntryToRemove.EffectiveUpdateTime);
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
                _lastDefunctSiloCleanupTime = now;
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

        private bool ShouldCleanupExpiredSilos(MembershipTableSnapshot membership, DateTimeOffset now, DateTimeOffset beforeDate, TimeSpan cleanupPeriod)
        {
            if (!_lastDefunctSiloCleanupTime.HasValue || now - _lastDefunctSiloCleanupTime.Value >= cleanupPeriod)
            {
                return true;
            }

            foreach (var entry in membership.Entries.Values)
            {
                if (entry.Status != SiloStatus.Active && entry.EffectiveIAmAliveTime < beforeDate.UtcDateTime)
                {
                    return true;
                }
            }

            return false;
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

            public static bool operator >(DefunctSiloEntryPriority left, DefunctSiloEntryPriority right) => Compare(left, right) > 0;

            public static bool operator <(DefunctSiloEntryPriority left, DefunctSiloEntryPriority right) => Compare(left, right) < 0;

            public int CompareTo(DefunctSiloEntryPriority other) => Compare(this, other);

            private static int Compare(DefunctSiloEntryPriority left, DefunctSiloEntryPriority right)
            {
                var result = left._entry.EffectiveUpdateTime.CompareTo(right._entry.EffectiveUpdateTime);
                return result != 0 ? result : left._entry.SiloAddress.CompareTo(right._entry.SiloAddress);
            }
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
