using DistributedTests.GrainInterfaces;
using Orleans;
using System.Threading.Tasks;

namespace DistributedTests.Grains
{
    public class PingGrain : Grain, IPingGrain
    {
        public ValueTask Ping() => default;
    }
}
