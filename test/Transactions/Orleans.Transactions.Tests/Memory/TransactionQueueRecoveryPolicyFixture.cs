using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit;
using TestExtensions;

namespace Orleans.Transactions.Tests;

public sealed class TransactionQueueRecoveryPolicyFixture : BaseTestClusterFixture
{
    internal TransactionQueueRecoveryPolicyStorageController StorageController =>
        ((InProcessSiloHandle)this.HostedCluster.Primary!).ServiceProvider.GetRequiredService<TransactionQueueRecoveryPolicyStorageController>();

    internal TransactionQueueRecoveryPolicyActivationTracker ActivationTracker =>
        ((InProcessSiloHandle)this.HostedCluster.Primary!).ServiceProvider.GetRequiredService<TransactionQueueRecoveryPolicyActivationTracker>();

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
    }

    public sealed class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .ConfigureServices(services =>
                {
                    services.AddKeyedSingleton<IRemoteCommitService, RemoteCommitService>(TransactionTestConstants.RemoteCommitService);
                    services.AddSingleton<TransactionQueueRecoveryPolicyStorageController>();
                    services.AddSingleton<TransactionQueueRecoveryPolicyActivationTracker>();
                    services.AddOptions<MemoryGrainStorageOptions>(TransactionTestConstants.TransactionStore)
                        .Configure(options => options.NumStorageGrains = 1);
                    services.AddTransient<IPostConfigureOptions<MemoryGrainStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<MemoryGrainStorageOptions>>();
                    services.ConfigureNamedOptionForLogging<MemoryGrainStorageOptions>(TransactionTestConstants.TransactionStore);
                    services.AddGrainStorage(
                        TransactionTestConstants.TransactionStore,
                        (serviceProvider, name) => new TransactionQueueRecoveryPolicyGrainStorage(
                            MemoryGrainStorageFactory.Create(serviceProvider, name),
                            serviceProvider.GetRequiredService<TransactionQueueRecoveryPolicyStorageController>()));
                })
                .UseTransactions();
        }
    }
}

internal sealed class TransactionQueueRecoveryPolicyGrainStorage(IGrainStorage inner, TransactionQueueRecoveryPolicyStorageController controller) : IGrainStorage, IDisposable
{
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        controller.ThrowIfConfigured(TransactionQueueRecoveryPolicyStorageController.StorageOperation.Read, grainId);
        return inner.ReadStateAsync(stateName, grainId, grainState);
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        controller.ThrowIfConfigured(TransactionQueueRecoveryPolicyStorageController.StorageOperation.Write, grainId);
        return inner.WriteStateAsync(stateName, grainId, grainState);
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        return inner.ClearStateAsync(stateName, grainId, grainState);
    }

    public void Dispose()
    {
        (inner as IDisposable)?.Dispose();
    }
}

internal sealed class TransactionQueueRecoveryPolicyStorageController
{
    public enum StorageOperation
    {
        Read,
        Write
    }

    private readonly object syncLock = new();
    private readonly Dictionary<(StorageOperation Operation, GrainId GrainId), Queue<Exception>> faults = [];

    public void Reset()
    {
        lock (this.syncLock)
        {
            this.faults.Clear();
        }
    }

    public void EnqueueReadFaults(GrainId grainId, int count, Func<int, Exception> exceptionFactory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentNullException.ThrowIfNull(exceptionFactory);

        for (var i = 1; i <= count; i++)
        {
            this.EnqueueFault(StorageOperation.Read, grainId, exceptionFactory(i));
        }
    }

    public void EnqueueWriteFault(GrainId grainId, Exception exception)
    {
        this.EnqueueFault(StorageOperation.Write, grainId, exception);
    }

    public void ThrowIfConfigured(StorageOperation operation, GrainId grainId)
    {
        Exception? exception = null;

        lock (this.syncLock)
        {
            if (this.faults.TryGetValue((operation, grainId), out var queue) && queue.Count > 0)
            {
                exception = queue.Dequeue();
                if (queue.Count == 0)
                {
                    this.faults.Remove((operation, grainId));
                }
            }
        }

        if (exception is not null)
        {
            throw exception;
        }
    }

    private void EnqueueFault(StorageOperation operation, GrainId grainId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (this.syncLock)
        {
            if (!this.faults.TryGetValue((operation, grainId), out var queue))
            {
                queue = new Queue<Exception>();
                this.faults[(operation, grainId)] = queue;
            }

            queue.Enqueue(exception);
        }
    }
}

internal sealed class TransactionQueueRecoveryPolicyActivationTracker
{
    private readonly object syncLock = new();
    private readonly Dictionary<GrainId, int> activations = [];
    private readonly Dictionary<GrainId, int> deactivations = [];
    private readonly List<Waiter> waiters = [];

    public void Reset()
    {
        lock (this.syncLock)
        {
            this.activations.Clear();
            this.deactivations.Clear();
            this.waiters.Clear();
        }
    }

    public int GetActivationCount(GrainId grainId)
    {
        lock (this.syncLock)
        {
            return this.activations.TryGetValue(grainId, out var count) ? count : 0;
        }
    }

    public int GetDeactivationCount(GrainId grainId)
    {
        lock (this.syncLock)
        {
            return this.deactivations.TryGetValue(grainId, out var count) ? count : 0;
        }
    }

    public Task WaitForActivationCountAsync(GrainId grainId, int expectedCount, TimeSpan? timeout = null)
    {
        return this.WaitForCountAsync(grainId, WaiterKind.Activation, expectedCount, timeout);
    }

    public Task WaitForDeactivationCountAsync(GrainId grainId, int expectedCount, TimeSpan? timeout = null)
    {
        return this.WaitForCountAsync(grainId, WaiterKind.Deactivation, expectedCount, timeout);
    }

    public void RecordActivated(GrainId grainId)
    {
        this.Record(grainId, WaiterKind.Activation, this.activations);
    }

    public void RecordDeactivated(GrainId grainId)
    {
        this.Record(grainId, WaiterKind.Deactivation, this.deactivations);
    }

    private Task WaitForCountAsync(GrainId grainId, WaiterKind kind, int expectedCount, TimeSpan? timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedCount, 1);

        Task task;
        lock (this.syncLock)
        {
            if (this.GetCountNoLock(grainId, kind) >= expectedCount)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.waiters.Add(new Waiter(grainId, kind, expectedCount, completion));
            task = completion.Task;
        }

        return task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
    }

    private void Record(GrainId grainId, WaiterKind kind, Dictionary<GrainId, int> counts)
    {
        List<TaskCompletionSource<object?>> toComplete = [];

        lock (this.syncLock)
        {
            counts[grainId] = this.GetCountNoLock(grainId, kind) + 1;
            var currentCount = counts[grainId];

            for (var i = this.waiters.Count - 1; i >= 0; i--)
            {
                var waiter = this.waiters[i];
                if (waiter.GrainId == grainId && waiter.Kind == kind && waiter.ExpectedCount <= currentCount)
                {
                    toComplete.Add(waiter.Completion);
                    this.waiters.RemoveAt(i);
                }
            }
        }

        foreach (var completion in toComplete)
        {
            completion.TrySetResult(null);
        }
    }

    private int GetCountNoLock(GrainId grainId, WaiterKind kind)
    {
        var counts = kind == WaiterKind.Activation ? this.activations : this.deactivations;
        return counts.TryGetValue(grainId, out var count) ? count : 0;
    }

    private sealed record Waiter(GrainId GrainId, WaiterKind Kind, int ExpectedCount, TaskCompletionSource<object?> Completion);

    private enum WaiterKind
    {
        Activation,
        Deactivation
    }
}

[GenerateSerializer]
public sealed class TransactionQueueRecoveryPolicyState
{
    [Id(0)]
    public int Value { get; set; }
}

public interface ITransactionQueueRecoveryPolicyGrain : IGrainWithStringKey
{
    Task<Guid> GetActivationId();

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<int> Add(int delta);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<int> GetValue();
}

internal sealed class TransactionQueueRecoveryPolicyGrain(
    [TransactionalState("state", TransactionTestConstants.TransactionStore)]
    ITransactionalState<TransactionQueueRecoveryPolicyState> state,
    TransactionQueueRecoveryPolicyActivationTracker activationTracker) : Grain, ITransactionQueueRecoveryPolicyGrain
{
    private readonly Guid activationId = Guid.NewGuid();

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        activationTracker.RecordActivated(this.GetGrainId());
        return base.OnActivateAsync(cancellationToken);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        activationTracker.RecordDeactivated(this.GetGrainId());
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<Guid> GetActivationId() => Task.FromResult(this.activationId);

    public Task<int> Add(int delta)
    {
        return state.PerformUpdate(currentState =>
        {
            currentState.Value += delta;
            return currentState.Value;
        });
    }

    public Task<int> GetValue() => state.PerformRead(currentState => currentState.Value);
}
