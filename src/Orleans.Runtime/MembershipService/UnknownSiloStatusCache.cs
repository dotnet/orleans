using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Caching;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Classifies silos which remain absent after a fresh membership refresh as dead,
/// and leaves their status unknown when validation fails.
/// </summary>
internal sealed partial class UnknownSiloStatusCache
{
    internal const int CacheCapacity = 1_024;
    private readonly ConcurrentLruCache<SiloAddress, SiloStatusCacheEntry> _siloStatuses = new(CacheCapacity);
    private readonly object _refreshLock = new();
    private readonly IMembershipManager _membershipManager;
    private readonly ILogger _logger;
    private RefreshOperation? _activeRefresh;
    private RefreshOperation? _latestRefresh;
    private RefreshOperation? _pendingRefresh;
    private long _startedRefreshGeneration;

    public UnknownSiloStatusCache(IMembershipManager membershipManager, ILogger<UnknownSiloStatusCache> logger)
    {
        _membershipManager = membershipManager;
        _logger = logger;
    }

    public async ValueTask<Dictionary<SiloAddress, SiloStatus>> GetSiloStatuses(
        ClusterMembershipSnapshot snapshot,
        IReadOnlySet<SiloAddress> siloAddresses,
        CancellationToken cancellationToken)
        => (await ValidateSiloStatuses(
            snapshot,
            siloAddresses,
            cancellationToken,
            requireFresh: false)).Statuses;

    public async ValueTask<SiloStatusValidationResult> ValidateSiloStatuses(
        ClusterMembershipSnapshot snapshot,
        IReadOnlySet<SiloAddress> siloAddresses,
        CancellationToken cancellationToken,
        bool requireFresh = false)
    {
        var result = new Dictionary<SiloAddress, SiloStatus>();
        Dictionary<SiloAddress, SiloStatusCacheEntry>? observedStatuses = null;
        List<SiloAddress>? unknownSilos = null;
        foreach (var siloAddress in siloAddresses)
        {
            var status = snapshot.GetSiloStatus(siloAddress);
            if (status != SiloStatus.None)
            {
                var observedStatus = UpdateCachedStatus(siloAddress, status, snapshot.Version);
                result.Add(siloAddress, observedStatus.Status);
                observedStatuses ??= [];
                observedStatuses[siloAddress] = observedStatus;
            }
            else if (_siloStatuses.TryGet(siloAddress, out var cachedStatus)
                && cachedStatus.Status == SiloStatus.Dead)
            {
                result.Add(siloAddress, SiloStatus.Dead);
                observedStatuses ??= [];
                observedStatuses[siloAddress] = cachedStatus;
            }
            else
            {
                unknownSilos ??= [];
                unknownSilos.Add(siloAddress);
            }
        }

        if (requireFresh)
        {
            unknownSilos = [.. siloAddresses];
        }

        if (unknownSilos is null)
        {
            return new(result, snapshot);
        }

        try
        {
            var refresh = GetRefreshOperation(
                requireFresh ? [.. siloAddresses] : unknownSilos,
                observedStatuses,
                cancellationToken);
            var refreshedTableSnapshot = await refresh.Completion.Task.WaitAsync(cancellationToken);
            result.Clear();
            foreach (var siloAddress in siloAddresses)
            {
                if (refresh.TryGetStatus(siloAddress, out var refreshedStatus))
                {
                    result.Add(siloAddress, refreshedStatus.Status);
                    continue;
                }

                var status = refreshedTableSnapshot.GetSiloStatus(siloAddress);
                if (status == SiloStatus.None)
                {
                    status = SiloStatus.Dead;
                }

                result.Add(
                    siloAddress,
                    UpdateCachedStatus(siloAddress, status, refreshedTableSnapshot.Version).Status);
            }

            snapshot = refreshedTableSnapshot.CreateClusterMembershipSnapshot();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var siloAddress in unknownSilos)
            {
                result[siloAddress] = SiloStatus.None;
            }
        }

        return new(result, snapshot);
    }

    private RefreshOperation GetRefreshOperation(
        IReadOnlyList<SiloAddress> unknownSilos,
        IReadOnlyDictionary<SiloAddress, SiloStatusCacheEntry>? observedStatuses,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The started generation is the freshness boundary. Calls which observe the same generation can
        // share the next read; calls made after that read starts require the queued generation.
        var requiredGeneration = Volatile.Read(ref _startedRefreshGeneration) + 1;

        lock (_refreshLock)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_latestRefresh is { } latest && latest.Generation >= requiredGeneration)
            {
                latest.AddSilos(unknownSilos, observedStatuses);
                return latest;
            }

            if (_pendingRefresh is { } pending && pending.Generation >= requiredGeneration)
            {
                pending.AddSilos(unknownSilos, observedStatuses);
                return pending;
            }

            var generation = Math.Max(
                requiredGeneration,
                (_pendingRefresh ?? _activeRefresh)?.Generation + 1 ?? requiredGeneration);
            var operation = new RefreshOperation(generation, unknownSilos, observedStatuses);
            if (_activeRefresh is null)
            {
                StartRefresh(operation);
            }
            else
            {
                _pendingRefresh = operation;
            }

            return operation;
        }
    }

    private void StartRefresh(RefreshOperation operation)
    {
        _activeRefresh = _latestRefresh = operation;
        Volatile.Write(ref _startedRefreshGeneration, operation.Generation);
        _ = ExecuteRefresh(operation);
    }

    private async Task ExecuteRefresh(RefreshOperation operation)
    {
        try
        {
            // Caller cancellation only cancels that caller's wait. Membership shutdown owns the shared refresh.
            await _membershipManager.Refresh(
                targetVersion: null,
                cancellationToken: CancellationToken.None,
                requireFresh: true);

            var snapshot = _membershipManager.CurrentSnapshot;
            lock (_refreshLock)
            {
                foreach (var siloAddress in operation.SiloAddresses)
                {
                    var status = snapshot.GetSiloStatus(siloAddress);
                    if (status == SiloStatus.None)
                    {
                        status = SiloStatus.Dead;
                    }

                    var observedStatus = operation.Observe(
                        siloAddress,
                        new SiloStatusCacheEntry(status, snapshot.Version));
                    UpdateCachedStatus(siloAddress, observedStatus.Status, observedStatus.Version);
                }

                operation.Completion.TrySetResult(snapshot);
            }
        }
        catch (OperationCanceledException exception)
        {
            operation.Completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            LogWarningUnableToValidateUnknownSilos(_logger, exception);
            operation.Completion.TrySetException(exception);
        }
        finally
        {
            lock (_refreshLock)
            {
                if (ReferenceEquals(_activeRefresh, operation))
                {
                    _activeRefresh = null;
                }

                if (_pendingRefresh is { } pending)
                {
                    _pendingRefresh = null;
                    StartRefresh(pending);
                }
            }
        }
    }

    private SiloStatusCacheEntry UpdateCachedStatus(
        SiloAddress siloAddress,
        SiloStatus status,
        MembershipVersion version)
    {
        lock (_refreshLock)
        {
            var replacement = new SiloStatusCacheEntry(status, version);
            if (_siloStatuses.TryGet(siloAddress, out var existing)
                && (existing.Version > version
                    || (existing.Version == version
                        && existing.Status != SiloStatus.Dead
                        && status == SiloStatus.Dead)))
            {
                replacement = existing;
            }
            else
            {
                _siloStatuses.AddOrUpdate(siloAddress, replacement);
            }

            _activeRefresh?.Observe(siloAddress, replacement);
            if (!ReferenceEquals(_latestRefresh, _activeRefresh))
            {
                _latestRefresh?.Observe(siloAddress, replacement);
            }

            _pendingRefresh?.Observe(siloAddress, replacement);
            return replacement;
        }
    }

    private readonly record struct SiloStatusCacheEntry(
        SiloStatus Status,
        MembershipVersion Version);

    internal readonly record struct SiloStatusValidationResult(
        Dictionary<SiloAddress, SiloStatus> Statuses,
        ClusterMembershipSnapshot Snapshot);

    private sealed class RefreshOperation
    {
        private readonly HashSet<SiloAddress> _siloAddresses;
        private readonly Dictionary<SiloAddress, SiloStatusCacheEntry> _observedStatuses = [];

        public RefreshOperation(
            long generation,
            IReadOnlyList<SiloAddress> siloAddresses,
            IReadOnlyDictionary<SiloAddress, SiloStatusCacheEntry>? observedStatuses)
        {
            Generation = generation;
            _siloAddresses = [.. siloAddresses];
            AddObservations(observedStatuses);
        }

        public TaskCompletionSource<MembershipTableSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Generation { get; }

        public IEnumerable<SiloAddress> SiloAddresses => _siloAddresses;

        public void AddSilos(
            IReadOnlyList<SiloAddress> siloAddresses,
            IReadOnlyDictionary<SiloAddress, SiloStatusCacheEntry>? observedStatuses)
        {
            foreach (var siloAddress in siloAddresses)
            {
                _siloAddresses.Add(siloAddress);
            }

            AddObservations(observedStatuses);
        }

        public SiloStatusCacheEntry Observe(SiloAddress siloAddress, SiloStatusCacheEntry status)
        {
            lock (_observedStatuses)
            {
                if (!_siloAddresses.Contains(siloAddress))
                {
                    return status;
                }

                if (_observedStatuses.TryGetValue(siloAddress, out var existing)
                    && (existing.Version > status.Version
                        || (existing.Version == status.Version
                            && existing.Status != SiloStatus.Dead
                            && status.Status == SiloStatus.Dead)))
                {
                    return existing;
                }

                _observedStatuses[siloAddress] = status;
                return status;
            }
        }

        public bool TryGetStatus(SiloAddress siloAddress, out SiloStatusCacheEntry status)
        {
            lock (_observedStatuses)
            {
                return _observedStatuses.TryGetValue(siloAddress, out status);
            }
        }

        private void AddObservations(
            IReadOnlyDictionary<SiloAddress, SiloStatusCacheEntry>? observedStatuses)
        {
            if (observedStatuses is null)
            {
                return;
            }

            foreach (var (siloAddress, status) in observedStatuses)
            {
                Observe(siloAddress, status);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unable to validate unknown silos against cluster membership"
    )]
    private static partial void LogWarningUnableToValidateUnknownSilos(
        ILogger logger,
        Exception exception);
}
