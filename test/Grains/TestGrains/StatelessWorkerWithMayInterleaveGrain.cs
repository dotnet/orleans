using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

[StatelessWorker(1)] // '1' to force interleaving, otherwise it just creates a new worker
[MayInterleave(nameof(MayInterleaveMethod))]
public class StatelessWorkerWithMayInterleaveGrain : Grain, IStatelessWorkerWithMayInterleaveGrain
{
    private TimeSpan _delay = TimeSpan.FromMilliseconds(1);

    public static bool MayInterleaveMethod(IInvokable req) => req.GetMethodName() == nameof(GoFast);

    public Task SetDelay(TimeSpan delay)
    {
        _delay = delay;
        return Task.CompletedTask;
    }

    public Task GoSlow() => Task.Delay(_delay);
    public Task GoFast() => Task.Delay(_delay);
}