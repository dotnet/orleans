using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Grains
{
    public class CounterGrain : Grain, ICounterGrain
    {
        private IPersistentState<Counter> counter;

        public CounterGrain([PersistentState("Counter")] IPersistentState<Counter> counter)
        {
            this.counter = counter;
        }

        public Task IncrementAsync()
        {
            counter.State.Value += 1;

            return Task.CompletedTask;
        }

        public Task<int> GetValueAsync() => Task.FromResult(counter.State.Value);

        public class Counter
        {
            public int Value { get; set; }
        }
    }
}