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
    private const int CacheCapacity = 1_024;
    private readonly ConcurrentLruCache<SiloAddress, byte> _deadSilos = new(CacheCapacity);
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
    {
        var result = new Dictionary<SiloAddress, SiloStatus>();
        List<SiloAddress>? unknownSilos = null;
        foreach (var siloAddress in siloAddresses)
        {
            var status = snapshot.GetSiloStatus(siloAddress);
            if (status != SiloStatus.None)
            {
                _deadSilos.TryRemove(siloAddress);
                result.Add(siloAddress, status);
            }
            else if (_deadSilos.TryGet(siloAddress, out _))
            {
                result.Add(siloAddress, SiloStatus.Dead);
            }
            else
            {
                unknownSilos ??= [];
                unknownSilos.Add(siloAddress);
            }
        }

        if (unknownSilos is null)
        {
            return result;
        }

        try
        {
            var refresh = GetRefreshOperation(unknownSilos, cancellationToken);
            var refreshedSnapshot = await refresh.Completion.Task.WaitAsync(cancellationToken);
            foreach (var siloAddress in unknownSilos)
            {
                if (_deadSilos.TryGet(siloAddress, out _))
                {
                    result.Add(siloAddress, SiloStatus.Dead);
                    continue;
                }

                var status = refreshedSnapshot.GetSiloStatus(siloAddress);
                if (status == SiloStatus.None)
                {
                    status = SiloStatus.Dead;
                    _deadSilos.AddOrUpdate(siloAddress, 0);
                }

                result.Add(siloAddress, status);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var siloAddress in unknownSilos)
            {
                result.Add(siloAddress, SiloStatus.None);
            }
        }

        return result;
    }

    private RefreshOperation GetRefreshOperation(
        IReadOnlyList<SiloAddress> unknownSilos,
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
                latest.AddSilos(unknownSilos);
                return latest;
            }

            if (_pendingRefresh is { } pending && pending.Generation >= requiredGeneration)
            {
                pending.AddSilos(unknownSilos);
                return pending;
            }

            var generation = Math.Max(
                requiredGeneration,
                (_pendingRefresh ?? _activeRefresh)?.Generation + 1 ?? requiredGeneration);
            var operation = new RefreshOperation(generation, unknownSilos);
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
                        _deadSilos.AddOrUpdate(siloAddress, 0);
                    }
                    else
                    {
                        _deadSilos.TryRemove(siloAddress);
                    }
                }
            }

            operation.Completion.TrySetResult(snapshot);
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

    private sealed class RefreshOperation
    {
        private readonly HashSet<SiloAddress> _siloAddresses;

        public RefreshOperation(long generation, IReadOnlyList<SiloAddress> siloAddresses)
        {
            Generation = generation;
            _siloAddresses = [.. siloAddresses];
        }

        public TaskCompletionSource<MembershipTableSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Generation { get; }

        public IEnumerable<SiloAddress> SiloAddresses => _siloAddresses;

        public void AddSilos(IReadOnlyList<SiloAddress> siloAddresses)
        {
            foreach (var siloAddress in siloAddresses)
            {
                _siloAddresses.Add(siloAddress);
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
