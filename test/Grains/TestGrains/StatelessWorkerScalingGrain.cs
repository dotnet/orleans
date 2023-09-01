using System.Collections.Concurrent;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class StatelessWorkerScalingGrainSharedState
{
    public SemaphoreSlim Semaphore { get; } = new(0);
    public ConcurrentDictionary<GrainId, int> ActivationCounts { get; } = new();
    public ConcurrentDictionary<GrainId, int> WaitingActivations { get; } = new();
}

[StatelessWorker(maxLocalWorkers: 4)]
public class StatelessWorkerScalingGrain : Grain, IStatelessWorkerScalingGrain
{
    private readonly StatelessWorkerScalingGrainSharedState _shared;

    public StatelessWorkerScalingGrain(StatelessWorkerScalingGrainSharedState shared)
    {
        _shared = shared;
        _shared.ActivationCounts.AddOrUpdate(this.GetGrainId(), 1, (k, v) => v + 1);
        _shared.WaitingActivations.TryAdd(this.GetGrainId(), 0);
    }

    public async Task Wait()
    {
        _shared.WaitingActivations.AddOrUpdate(this.GetGrainId(), 1, (k, v) => v + 1);
        await _shared.Semaphore.WaitAsync();
        _shared.WaitingActivations.AddOrUpdate(this.GetGrainId(), 0, (k, v) => v - 1);
    }

    public Task Release()
    {
        _shared.Semaphore.Release();
        return Task.CompletedTask;
    }

    public Task<int> GetActivationCount() => Task.FromResult(_shared.ActivationCounts[this.GetGrainId()]);
    public Task<int> GetWaitingCount() => Task.FromResult(_shared.WaitingActivations[this.GetGrainId()]);
}
