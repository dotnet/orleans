using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

[StatelessWorker]
public class StatelessWorkerScalingGrain : Grain, IStatelessWorkerScalingGrain
{
    private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);
    private static ConcurrentDictionary<long, int> _activationCounter = new();
    private int _activation;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _activation = _activationCounter.AddOrUpdate(this.GetPrimaryKeyLong(), 1, (k, v) => v + 1);
        return base.OnActivateAsync(cancellationToken);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _semaphore.Dispose();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task Wait()
    {        
        _semaphore.Wait();
        return Task.CompletedTask;
    }

    public Task Release()
    {
        _semaphore.Release();
        return Task.CompletedTask;
    }

    public Task<int> GetActivation() => Task.FromResult(_activation);
}
