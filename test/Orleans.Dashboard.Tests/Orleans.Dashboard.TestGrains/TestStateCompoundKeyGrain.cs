using System;
using System.Threading.Tasks;
using Orleans;

namespace TestGrains
{
    public interface ITestStateCompoundKeyGrain : IGrainWithIntegerCompoundKey
    {
        Task<InMemoryCounterState> GetState();
    }

    public class TestStateCompoundKeyGrain : Grain, ITestStateCompoundKeyGrain
    {
        private readonly Random _random = new();
        private readonly InMemoryCounterState _state;

        public TestStateCompoundKeyGrain()
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
}