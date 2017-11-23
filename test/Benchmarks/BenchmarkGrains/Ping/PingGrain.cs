using Orleans;
using BenchmarkGrainInterfaces.Ping;
using System.Threading.Tasks;

namespace BenchmarkGrains.Ping
{
    public class PingGrain : Grain, IPingGrain
    {
        public Task Run()
        {
            return Task.CompletedTask;
        }
    }
}
