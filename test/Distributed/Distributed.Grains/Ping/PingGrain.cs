using Distributed.GrainInterfaces.Ping;
using Orleans;
using System.Threading.Tasks;

namespace Distributed.Grains.Ping
{
    public class PingGrain : Grain, IPingGrain
    {
        public Task Ping() => Task.CompletedTask;
    }
}
