using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class ValueTypeTestGrain : Grain<System.Int64>, IValueTypeTestGrain
    {
        public ValueTypeTestGrain()
        {
        }

        public async Task<System.Int64> GetStateData()
        {
            await ReadStateAsync();
            return State;
        }

        public Task SetStateData(System.Int64 d)
        {
            State = d;
            return WriteStateAsync();
        }
    }
}
