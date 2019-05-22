using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public class CounterGrain : Grain, ICounterGrain
    {
        private int counter;

        public Task IncrementAsync()
        {
            ++counter;
            return Task.CompletedTask;
        }

        public Task<int> GetValueAsync() => Task.FromResult(counter);
    }
}
