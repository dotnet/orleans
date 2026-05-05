using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Scheduler;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// A system target that implements <see cref="IRemoteGrainDirectory"/> by delegating to <see cref="DistributedGrainDirectory"/>.
/// This enables silos running the old <see cref="LocalGrainDirectory"/> to forward directory requests to silos running the
/// new <see cref="DistributedGrainDirectory"/> during a rolling upgrade.
/// </summary>
internal sealed partial class DistributedRemoteGrainDirectory : SystemTarget, IRemoteGrainDirectory
{
    private const int MaxBatchDegreeOfParallelism = 32;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly DistributedGrainDirectory _directory;
    private readonly ILogger<DistributedRemoteGrainDirectory> _logger;
    private readonly DirectoryMembershipService _membershipService;
    private readonly Queue<(string Name, object State, Func<DistributedRemoteGrainDirectory, object, Task> Action)> _pendingOperations = new();
    private readonly AsyncLock _executorLock = new();

    private DistributedRemoteGrainDirectory(
        DistributedGrainDirectory directory,
        DirectoryMembershipService membershipService,
        GrainType grainType,
        SystemTargetShared shared)
        : base(grainType, shared)
    {
        _directory = directory;
        _logger = shared.LoggerFactory.CreateLogger<DistributedRemoteGrainDirectory>();
        _membershipService = membershipService;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    /// <summary>
    /// Creates the pair of system targets that replace <see cref="RemoteGrainDirectory"/> when
    /// <see cref="DistributedGrainDirectory"/> is active: one for <see cref="Constants.DirectoryServiceType"/>
    /// and one for <see cref="Constants.DirectoryCacheValidatorType"/>.
    /// </summary>
    internal static (DistributedRemoteGrainDirectory DirectoryService, DistributedRemoteGrainDirectory CacheValidator)
        Create(DistributedGrainDirectory directory, DirectoryMembershipService membershipService, SystemTargetShared shared)
    {
        var directoryService = new DistributedRemoteGrainDirectory(directory, membershipService, Constants.DirectoryServiceType, shared);
        var cacheValidator = new DistributedRemoteGrainDirectory(directory, membershipService, Constants.DirectoryCacheValidatorType, shared);
        return (directoryService, cacheValidator);
    }

    /// <summary>
    /// Ensures the directory has an initialized membership view before processing requests.
    /// Without this, calls arriving before the directory processes its first membership update
    /// would block indefinitely in <see cref="DistributedGrainDirectory"/>'s internal retry loop.
    /// </summary>
    private async Task EnsureDirectoryInitializedAsync(CancellationToken cancellationToken)
    {
        if (_membershipService.CurrentView.Version == MembershipVersion.MinValue)
        {
            await _membershipService.RefreshViewAsync(new MembershipVersion(1), cancellationToken);
        }
    }

    private static ParallelOptions CreateParallelOptions(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        MaxDegreeOfParallelism = MaxBatchDegreeOfParallelism,
        TaskScheduler = TaskScheduler.Current,
    };

    private CancellationTokenSource CreateTimeoutCts() => new(OperationTimeout);

    private CancellationTokenSource CreateTimeoutCts(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _directory.OnStoppedToken);
        cts.CancelAfter(OperationTimeout);
        return cts;
    }

    private async Task RunBatchOperationAsync<T>(List<T> values, Func<T, CancellationToken, Task> operation)
    {
        using (var cts = CreateTimeoutCts(_directory.OnStoppedToken))
        {
            await EnsureDirectoryInitializedAsync(cts.Token);
        }

        var options = CreateParallelOptions(_directory.OnStoppedToken);
        await Parallel.ForEachAsync(values, options, async (value, cancellationToken) =>
        {
            using var cts = CreateTimeoutCts(cancellationToken);
            await operation(value, cts.Token);
        });
    }

    private IInternalGrainFactory GrainFactory => ActivationServices.GetRequiredService<IInternalGrainFactory>();

    private ISiloStatusOracle SiloStatusOracle => ActivationServices.GetRequiredService<ISiloStatusOracle>();

    private void EnqueueOperation(string name, object state, Func<DistributedRemoteGrainDirectory, object, Task> action)
    {
        lock (_pendingOperations)
        {
            _pendingOperations.Enqueue((name, state, action));
            if (_pendingOperations.Count <= 2)
            {
                WorkItemGroup.QueueTask(ExecutePendingOperations, this);
            }
        }
    }

    private async Task ExecutePendingOperations()
    {
        using (await _executorLock.LockAsync())
        {
            while (!_directory.OnStoppedToken.IsCancellationRequested)
            {
                (string Name, object State, Func<DistributedRemoteGrainDirectory, object, Task> Action) op;
                lock (_pendingOperations)
                {
                    if (_pendingOperations.Count == 0)
                    {
                        break;
                    }

                    op = _pendingOperations.Peek();
                }

                try
                {
                    await op.Action(this, op.State);
                    lock (_pendingOperations)
                    {
                        _pendingOperations.Dequeue();
                    }
                }
                catch (Exception exception) when (!_directory.OnStoppedToken.IsCancellationRequested)
                {
                    LogWarningOperationFailedRetry(_logger, exception, op.Name);
                    await Task.Delay(RetryDelay, _directory.OnStoppedToken).SuppressThrowing();
                }
            }
        }
    }

    private void DestroyDuplicateActivations(Dictionary<SiloAddress, List<GrainAddress>>? duplicates)
    {
        if (duplicates is null || duplicates.Count == 0)
        {
            return;
        }

        EnqueueOperation(
            nameof(DestroyDuplicateActivations),
            duplicates,
            static (self, state) => self.DestroyDuplicateActivationsAsync((Dictionary<SiloAddress, List<GrainAddress>>)state));
    }

    private async Task DestroyDuplicateActivationsAsync(Dictionary<SiloAddress, List<GrainAddress>> duplicates)
    {
        while (duplicates.Count > 0)
        {
            var pair = duplicates.First();
            if (SiloStatusOracle.GetApproximateSiloStatus(pair.Key) == SiloStatus.Active)
            {
                var remoteCatalog = GrainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, pair.Key);
                await remoteCatalog.DeleteActivations(
                    pair.Value,
                    DeactivationReasonCode.DuplicateActivation,
                    "This grain has been activated elsewhere");
            }

            duplicates.Remove(pair.Key);
        }
    }

    private async Task ProcessSplitPartitionRegistrationsAsync(SplitPartitionRegistrationBatch batch)
    {
        using (var cts = CreateTimeoutCts(_directory.OnStoppedToken))
        {
            await EnsureDirectoryInitializedAsync(cts.Token);
        }

        var pendingRegistrations = batch.PendingRegistrations;
        var winners = new GrainAddress?[pendingRegistrations.Count];
        var failures = new Exception?[pendingRegistrations.Count];
        var options = CreateParallelOptions(_directory.OnStoppedToken);
        await Parallel.ForEachAsync(Enumerable.Range(0, pendingRegistrations.Count), options, async (index, cancellationToken) =>
        {
            try
            {
                using var cts = CreateTimeoutCts(cancellationToken);
                winners[index] = await _directory.RegisterAsync(pendingRegistrations[index], null, cts.Token);
            }
            catch (Exception exception)
            {
                failures[index] = exception;
            }
        });

        Dictionary<SiloAddress, List<GrainAddress>>? duplicates = null;
        Exception? failure = null;
        for (var i = pendingRegistrations.Count - 1; i >= 0; i--)
        {
            if (failures[i] is not null)
            {
                failure ??= failures[i];
                continue;
            }

            var registration = pendingRegistrations[i];
            var winner = winners[i];
            if (winner is null || !winner.Equals(registration))
            {
                if (registration.SiloAddress is { } siloAddress)
                {
                    if (duplicates is null || !duplicates.TryGetValue(siloAddress, out var activations))
                    {
                        activations = [];
                        (duplicates ??= []).Add(siloAddress, activations);
                    }

                    activations.Add(registration);
                }
            }

            pendingRegistrations.RemoveAt(i);
        }

        DestroyDuplicateActivations(duplicates);

        if (failure is not null)
        {
            LogWarningAcceptSplitPartitionFailed(_logger, failure, Silo, pendingRegistrations.Count);
            throw failure;
        }

        LogInformationAcceptSplitPartitionCompleted(_logger, Silo, batch.InitialCount);
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = await _directory.RegisterAsync(address, null, cts.Token);
        return new(result, 0);
    }

    public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = await _directory.RegisterAsync(address, previousAddress, cts.Token);
        return new(result, 0);
    }

    public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var result = await _directory.LookupAsync(grainId, cts.Token);
        return new(result, 0);
    }

    public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        await UnregisterAsync(address, cause, cts.Token);
    }

    public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
    {
        await RunBatchOperationAsync(addresses, (address, cancellationToken) => UnregisterAsync(address, cause, cancellationToken));
    }

    private Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, CancellationToken cancellationToken)
    {
        switch (cause)
        {
            case UnregistrationCause.Force:
                return _directory.UnregisterAsync(address, cancellationToken);
            case UnregistrationCause.NonexistentActivation:
                // LocalGrainDirectory only removes these entries after LazyDeregistrationDelay.
                // This compatibility path does not track entry age, so preserve the conditional semantics by not force-removing the entry.
                return Task.CompletedTask;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, $"Deregistration cause {cause} is unknown and is not supported. This is a bug.");
        }
    }

    public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
    {
        using var cts = CreateTimeoutCts();
        await EnsureDirectoryInitializedAsync(cts.Token);
        var existing = await _directory.LookupAsync(grainId, cts.Token);
        if (existing is not null)
        {
            await _directory.UnregisterAsync(existing, cts.Token);
        }
    }

    public async Task RegisterMany(List<GrainAddress> addresses)
    {
        await RunBatchOperationAsync(addresses, (address, cancellationToken) => _directory.RegisterAsync(address, null, cancellationToken));
    }

    public async Task<List<AddressAndTag>> LookUpMany(List<(GrainId GrainId, int Version)> grainAndETagList)
    {
        LogInformationLookUpManyReceived(_logger, Silo, grainAndETagList.Count);

        using (var cts = CreateTimeoutCts(_directory.OnStoppedToken))
        {
            await EnsureDirectoryInitializedAsync(cts.Token);
        }

        var result = new AddressAndTag[grainAndETagList.Count];
        var options = CreateParallelOptions(_directory.OnStoppedToken);
        await Parallel.ForEachAsync(Enumerable.Range(0, grainAndETagList.Count), options, async (index, cancellationToken) =>
        {
            using var cts = CreateTimeoutCts(cancellationToken);
            var address = await _directory.LookupAsync(grainAndETagList[index].GrainId, cts.Token);
            result[index] = new(address, 0);
        });

        return [.. result];
    }

    public Task AcceptSplitPartition(List<GrainAddress> singleActivations)
    {
        LogInformationAcceptSplitPartitionStarted(_logger, Silo, singleActivations.Count);
        if (singleActivations.Count > 0)
        {
            EnqueueOperation(
                nameof(AcceptSplitPartition),
                new SplitPartitionRegistrationBatch([.. singleActivations]),
                static (self, state) => self.ProcessSplitPartitionRegistrationsAsync((SplitPartitionRegistrationBatch)state));
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rolling upgrade diagnostic: silo {Silo} received LookUpMany for {Count} entries."
    )]
    private static partial void LogInformationLookUpManyReceived(ILogger logger, SiloAddress silo, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rolling upgrade diagnostic: silo {Silo} accepted split-partition handoff for {Count} registrations."
    )]
    private static partial void LogInformationAcceptSplitPartitionStarted(ILogger logger, SiloAddress silo, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Rolling upgrade diagnostic: silo {Silo} completed split-partition handoff for {Count} registrations."
    )]
    private static partial void LogInformationAcceptSplitPartitionCompleted(ILogger logger, SiloAddress silo, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rolling upgrade diagnostic: silo {Silo} failed split-partition handoff for {Count} registrations."
    )]
    private static partial void LogWarningAcceptSplitPartitionFailed(ILogger logger, Exception exception, SiloAddress silo, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rolling upgrade compatibility operation {Operation} failed and will be retried."
    )]
    private static partial void LogWarningOperationFailedRetry(ILogger logger, Exception exception, string operation);

    private sealed class SplitPartitionRegistrationBatch(List<GrainAddress> pendingRegistrations)
    {
        public int InitialCount { get; } = pendingRegistrations.Count;
        public List<GrainAddress> PendingRegistrations { get; } = pendingRegistrations;
    }
}
