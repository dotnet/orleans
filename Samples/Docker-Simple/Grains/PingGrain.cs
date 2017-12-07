using GrainInterfaces;
using Orleans;
using System.Threading.Tasks;

namespace Grains
{
    public class PingGrain : Grain, IPingGrain
    {
        private int counter = 0;

        public Task<int> Ping()
        {
            return Task.FromResult(++this.counter);
        }
    }
}
