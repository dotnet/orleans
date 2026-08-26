using DistributedTests.GrainInterfaces;

namespace DistributedTests.Grains;

public class PingGrain : Grain, IPingGrain
{
    public ValueTask Ping(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return default;
    }
}
