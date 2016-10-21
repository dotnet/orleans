using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class CounterGrainState
    {
        public int Value { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class CounterGrain : Grain<CounterGrainState>, ICounterGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger($"CounterGrain[{this.GetPrimaryKey()}]");
            logger.Info("OnActivateAsync");
            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return base.OnDeactivateAsync();
        }

        public Task IncrementValue()
        {
            State.Value = State.Value + 1;
            return WriteStateAsync();
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Value);
        }

        public Task ResetValue()
        {
            State.Value = 0;
            return WriteStateAsync();
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }
    }
}
