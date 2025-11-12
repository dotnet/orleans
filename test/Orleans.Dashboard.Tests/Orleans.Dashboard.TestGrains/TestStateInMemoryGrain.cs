using System;
using System.Threading.Tasks;
using Orleans;

namespace TestGrains
{
    public interface ITestStateInMemoryGrain : IGrainWithIntegerKey
    {
        Task<InMemoryCounterState> GetState();
    }

    public class TestStateInMemoryGrain : Grain, ITestStateInMemoryGrain
    {
        private readonly Random _random = new();
        private readonly InMemoryCounterState _state;

        public TestStateInMemoryGrain()
        {
            _state = new InMemoryCounterState()
            {
                Counter = _random.Next(100),
                ActivatedDateTime = DateTime.UtcNow
            };
        }

        public Task<InMemoryCounterState> GetState()
        {
            return Task.FromResult(_state);
        }
    }

    [GenerateSerializer]
    public class InMemoryCounterState
    {
        [Id(0)]
        public int Counter { get; set; }
        [Id(1)]
        public DateTime ActivatedDateTime { get; set; }
    }
}