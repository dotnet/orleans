using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class ValueTypeTestGrain : Grain<ValueTypeTestData>, IValueTypeTestGrain
    {
        public ValueTypeTestGrain()
        {
        }

        public async Task<ValueTypeTestData> GetStateData()
        {
            await ReadStateAsync();
            return State;
        }

        public Task SetStateData(ValueTypeTestData d)
        {
            State = d;
            return WriteStateAsync();
        }
    }
}
