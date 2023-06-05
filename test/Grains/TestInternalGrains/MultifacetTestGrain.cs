using Orleans.Providers;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    [GenerateSerializer]
    public class MultifacetTestGrainState
    {
        [Id(0)]
        public int Value { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class MultifacetTestGrain : Grain<MultifacetTestGrainState>, IMultifacetTestGrain
    {

        public string GetRuntimeInstanceId() => RuntimeIdentity;

        public Task SetValue(int x)
        {
            State.Value = x;
            return Task.CompletedTask;
        }

        Task<int> IMultifacetReader.GetValue() => Task.FromResult(State.Value);
    }
}
