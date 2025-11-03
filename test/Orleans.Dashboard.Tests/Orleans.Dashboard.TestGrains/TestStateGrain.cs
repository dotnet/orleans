using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace TestGrains
{
    public interface ITestStateGrain : IGrainWithIntegerKey
    {
        Task<CounterState> GetCounterState();
        Task WriteCounterState(CounterState state);
    }

    public class TestStateGrain : Grain, ITestStateGrain
    {
        private readonly Random random = new();

        private readonly IPersistentState<CounterState> _counter;

        public TestStateGrain(
            [PersistentState("counter")]IPersistentState <CounterState> counter)
        {
            _counter = counter;
        }

        public Task<CounterState> GetCounterState()
        {
            return Task.FromResult(_counter.State);
        }

        public async Task WriteCounterState(CounterState state)
        {
            _counter.State = state;
            await _counter.WriteStateAsync();
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);
            _counter.State.Counter = random.Next(100);
            _counter.State.CurrentDateTime = DateTime.UtcNow;
            await _counter.WriteStateAsync();
        }
    }

    [GenerateSerializer]
    public class CounterState
    {
        [Id(0)]
        public int Counter { get; set; }
        [Id(1)]
        public DateTime CurrentDateTime { get; set; }
    }
}