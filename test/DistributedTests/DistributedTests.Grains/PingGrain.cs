using DistributedTests.GrainInterfaces;

namespace DistributedTests.Grains
{
    public class PingGrain : Grain, IPingGrain
    {
        public ValueTask Ping() => default;
    }
}
