#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

/// <summary>
/// Remote interface for migrating grain activations to a silo.
/// </summary>
internal interface IActivationMigrationManagerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Accepts migrating grains on a best-effort basis.
    /// </summary>
    ValueTask AcceptMigratingGrains([Immutable] List<GrainMigrationPackage> migratingGrains);
}

[GenerateSerializer, Immutable]
internal struct GrainMigrationPackage
{
    [Id(0)]
    public GrainId GrainId { get; set; }

    [Id(1)]
    public MigrationContext MigrationContext { get; set; }
}

/// <summary>
/// Functionality for migrating an activation to a new location.
/// </summary>
internal interface IActivationMigrationManager
{
    /// <summary>
    /// Attempts to migrate a grain to the specified target.
    /// </summary>
    /// <param name="target">The migration target.</param>
    /// <param name="grainId">The grain being migrated.</param>
    /// <param name="migrationContext">Information about the grain being migrated, which will be consumed by the new activation.</param>
    ValueTask MigrateAsync(SiloAddress target, GrainId grainId, MigrationContext migrationContext);
}

/// <summary>
/// Migrates grain activations to target hosts and handles migration requests from other hosts.
/// </summary>
internal class ActivationMigrationManager : SystemTarget, IActivationMigrationManagerSystemTarget, IActivationMigrationManager
{
    private const int MaxBatchSize = 1_000;
    private readonly ConcurrentDictionary<SiloAddress, (Task PumpTask, Channel<MigrationWorkItem> WorkItemChannel)> _workers = new();
    private readonly ObjectPool<MigrationWorkItem> _workItemPool = ObjectPool.Create(new MigrationWorkItem.ObjectPoolPolicy());
    private readonly ILogger<ActivationMigrationManager> _logger;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly Catalog _catalog;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly object _lock = new();

#pragma warning disable IDE0052 // Remove unread private members. Justification: this field is only for diagnostic purposes.
    private readonly Task? _membershipUpdatesTask;
#pragma warning restore IDE0052 // Remove unread private members

    public ActivationMigrationManager(
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IInternalGrainFactory grainFactory,
        Catalog catalog,
        IClusterMembershipService clusterMembershipService) : base(Constants.ActivationMigratorType, localSiloDetails.SiloAddress, loggerFactory)
    {
        _grainFactory = grainFactory;
        _logger = loggerFactory.CreateLogger<ActivationMigrationManager>();
        _catalog = catalog;
        _clusterMembershipService = clusterMembershipService;
        _catalog.RegisterSystemTarget(this);

        {
            using var _ = new ExecutionContextSuppressor();
            _membershipUpdatesTask = Task.Factory.StartNew(
                state => ((ActivationMigrationManager)state!).ProcessMembershipUpdates(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _membershipUpdatesTask.Ignore();
        }
    }

    public ValueTask AcceptMigratingGrains(List<GrainMigrationPackage> migratingGrains)
    {
        foreach (var package in migratingGrains)
        {
            // If the activation does not exist, create it and provide it with the migration context while doing so.
            // If the activation already exists or cannot be created, it is too late to perform migration, so ignore the request.
            _catalog.GetOrCreateActivation(package.GrainId, requestContextData: null, package.MigrationContext);
        }

        return default;
    }

    public ValueTask MigrateAsync(SiloAddress targetSilo, GrainId grainId, MigrationContext migrationContext)
    {
        var workItem = _workItemPool.Get();
        var migrationPackage = new GrainMigrationPackage { GrainId = grainId, MigrationContext = migrationContext };
        workItem.Initialize(migrationPackage);
        var workItemWriter = GetOrCreateWorker(targetSilo);
        if (!workItemWriter.TryWrite(workItem))
        {
            workItem.SetException(new SiloUnavailableException($"Silo {targetSilo} is no longer active"));
        }

        return workItem.AsValueTask();
    }

    private async Task ProcessMembershipUpdates()
    {
        await Task.Yield();

        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Monitoring cluster membership updates");
            }

            var previousSnapshot = _clusterMembershipService.CurrentSnapshot;
            await foreach (var snapshot in _clusterMembershipService.MembershipUpdates)
            {
                try
                {
                    var diff = snapshot.CreateUpdate(previousSnapshot);
                    previousSnapshot = snapshot;
                    foreach (var change in diff.Changes)
                    {
                        if (change.Status.IsTerminating())
                        {
                            RemoveWorker(change.SiloAddress);
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error processing cluster membership updates");
                }
            }
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("No longer monitoring cluster membership updates");
            }
        }
    }

    private async Task PumpMigrationQueue(SiloAddress targetSilo, Channel<MigrationWorkItem> workItems)
    {
        try
        {
            var remote = _grainFactory.GetSystemTarget<IActivationMigrationManagerSystemTarget>(Constants.ActivationMigratorType, targetSilo);
            await Task.Yield();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Starting migration worker for target silo {SiloAddress}", targetSilo);
            }

            var items = new List<MigrationWorkItem>();
            var batch = new List<GrainMigrationPackage>();
            var reader = workItems.Reader;
            while (await reader.WaitToReadAsync())
            {
                try
                {
                    // Collect a batch of work items.
                    while (batch.Count < MaxBatchSize && reader.TryRead(out var workItem))
                    {
                        items.Add(workItem);
                        batch.Add(workItem.Value);
                    }

                    // Attempt to migrate the batch.
                    await remote.AcceptMigratingGrains(batch);

                    foreach (var item in items)
                    {
                        item.SetCompleted();
                    }

                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Migrated {Count} activations to target silo {SiloAddress}", items.Count, targetSilo);
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error while migrating {Count} grain activations to {SiloAddress}", items.Count, targetSilo);

                    foreach (var item in items)
                    {
                        item.SetException(exception);
                    }

                    // If the silo is terminating, we should stop trying to migrate activations to it.
                    if (_clusterMembershipService.CurrentSnapshot.GetSiloStatus(targetSilo).IsTerminating())
                    {
                        break;
                    }
                }
                finally
                {
                    items.Clear();
                    batch.Clear();
                }
            }

            // Remove ourselves and clean up.
            RemoveWorker(targetSilo);
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Exiting migration worker for target silo {SiloAddress}", targetSilo);
            }
        }
    }

    private ChannelWriter<MigrationWorkItem> GetOrCreateWorker(SiloAddress targetSilo)
    {
        if (!_workers.TryGetValue(targetSilo, out var entry))
        {
            lock (_lock)
            {
                if (!_workers.TryGetValue(targetSilo, out entry))
                {
                    using var _ = new ExecutionContextSuppressor();
                    var channel = Channel.CreateUnbounded<MigrationWorkItem>();
                    var pumpTask = Task.Factory.StartNew(
                        () => PumpMigrationQueue(targetSilo, channel),
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        WorkItemGroup.TaskScheduler).Unwrap();
                    pumpTask.Ignore();

                    entry = (pumpTask, channel);
                    var didAdd = _workers.TryAdd(targetSilo, entry);
                    Debug.Assert(didAdd);
                }
            }
        }

        return entry.WorkItemChannel.Writer;
    }

    private void RemoveWorker(SiloAddress targetSilo)
    {
        if (_workers.TryRemove(targetSilo, out var entry))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Target silo {SiloAddress} is no longer active, so this migration activation worker is terminating", targetSilo);
            }

            entry.WorkItemChannel.Writer.TryComplete();

            var exception = new SiloUnavailableException($"Silo {targetSilo} is no longer active");
            while (entry.WorkItemChannel.Reader.TryRead(out var item))
            {
                item.SetException(exception);
            }
        }
    }

    private class MigrationWorkItem : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };
        private GrainMigrationPackage _migrationPackage;

        public void Initialize(GrainMigrationPackage package) => _migrationPackage = package;
        public void Reset() => _core.Reset();

        public GrainMigrationPackage Value => _migrationPackage;

        public void SetCompleted() => _core.SetResult(0);
        public void SetException(Exception exception) => _core.SetException(exception);
        public ValueTask AsValueTask() => new (this, _core.Version);

        public void GetResult(short token)
        {
            try
            {
                _core.GetResult(token);
            }
            finally
            {
                Reset();
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public sealed class ObjectPoolPolicy : IPooledObjectPolicy<MigrationWorkItem>
        {
            public MigrationWorkItem Create() => new();
            public bool Return(MigrationWorkItem obj)
            {
                obj.Reset();
                return true;
            }
        }
    }
}
