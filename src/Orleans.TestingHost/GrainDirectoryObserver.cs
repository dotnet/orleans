using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.TestingHost;

internal sealed class GrainDirectoryObserver : IObserver<GrainDirectoryEvents.GrainDirectoryEvent>, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, MembershipVersion> _localVersions = [];
    private readonly Dictionary<(SiloAddress SiloAddress, int PartitionIndex), MembershipVersion> _distributedVersions = [];
    private readonly HashSet<RangeOperation> _pendingRangeOperations = [];
    private readonly IDisposable _subscription;
    private TaskCompletionSource _changed = CreateCompletion();
    private Exception? _error;

    public GrainDirectoryObserver()
    {
        _subscription = GrainDirectoryEvents.AllEvents.Subscribe(this);
    }

    public async Task<bool> WaitForConvergenceAsync(
        IReadOnlyCollection<InProcessSiloHandle> activeSilos,
        TimeSpan timeout)
    {
        var targets = activeSilos.Select(CreateTarget).ToArray();
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_error is { } error)
                {
                    throw new InvalidOperationException("An error occurred while observing grain directory events.", error);
                }

                if (HasConverged(targets))
                {
                    return true;
                }

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    public void OnNext(GrainDirectoryEvents.GrainDirectoryEvent value)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            switch (value)
            {
                case GrainDirectoryEvents.MembershipVersionApplied applied:
                    UpdateVersion(_localVersions, applied.SiloAddress, applied.Version);
                    break;
                case GrainDirectoryEvents.MembershipVersionObserved observed:
                    UpdateVersion(
                        _distributedVersions,
                        (observed.SiloAddress, observed.PartitionIndex),
                        observed.Version);
                    break;
                case GrainDirectoryEvents.RangeOperationStarted started:
                    _pendingRangeOperations.Add(RangeOperation.From(started));
                    break;
                case GrainDirectoryEvents.RangeOperationCompleted completed:
                    _pendingRangeOperations.Remove(RangeOperation.From(completed));
                    break;
            }

            changed = _changed;
            _changed = CreateCompletion();
        }

        changed.TrySetResult();
    }

    public void OnError(Exception error)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            _error = error;
            changed = _changed;
            _changed = CreateCompletion();
        }

        changed.TrySetResult();
    }

    public void OnCompleted()
    {
    }

    public void Dispose() => _subscription.Dispose();

    private bool HasConverged(Target[] targets)
    {
        foreach (var target in targets)
        {
            if (!_localVersions.TryGetValue(target.SiloAddress, out var localVersion)
                || localVersion < target.Version)
            {
                return false;
            }

            if (target.DistributedPartitionCount > 0)
            {
                for (var partitionIndex = 0; partitionIndex < target.DistributedPartitionCount; partitionIndex++)
                {
                    if (!_distributedVersions.TryGetValue((target.SiloAddress, partitionIndex), out var version)
                        || version < target.Version)
                    {
                        return false;
                    }
                }

                if (_pendingRangeOperations.Any(operation => operation.SiloAddress.Equals(target.SiloAddress)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Target CreateTarget(InProcessSiloHandle silo)
    {
        var services = silo.ServiceProvider;
        var membershipVersion = services.GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Version;
        var distributedPartitionCount = services.GetService<DistributedGrainDirectory>() is not null
            ? services.GetRequiredService<IOptions<GrainDirectoryOptions>>().Value.PartitionsPerSilo
            : 0;
        return new(silo.SiloAddress, membershipVersion, distributedPartitionCount);
    }

    private static void UpdateVersion<TKey>(
        Dictionary<TKey, MembershipVersion> versions,
        TKey key,
        MembershipVersion version)
        where TKey : notnull
    {
        if (!versions.TryGetValue(key, out var current) || version > current)
        {
            versions[key] = version;
        }
    }

    private static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Target(
        SiloAddress SiloAddress,
        MembershipVersion Version,
        int DistributedPartitionCount);

    private readonly record struct RangeOperation(
        SiloAddress SiloAddress,
        int PartitionIndex,
        MembershipVersion Version,
        RingRange Range,
        string OperationName)
    {
        public static RangeOperation From(GrainDirectoryEvents.RangeOperationEvent operation) =>
            new(
                operation.SiloAddress,
                operation.PartitionIndex,
                operation.Version,
                operation.Range,
                operation.OperationName);
    }
}
