using OneBoxDeployment.GrainInterfaces;
using Orleans;
using Orleans.Providers;
using System.Threading.Tasks;

namespace OneBoxDeployment.Grains
{
    /// <inheritdoc />
    [StorageProvider(ProviderName = "TestStorage")]
    public class TestStateGrain: Grain<int>, ITestStateGrain
    {
        /// <inheritdoc />
        public async Task<int> Increment(int incrementBy)
        {
            unchecked { State += incrementBy; }
            await base.WriteStateAsync();

            return State;
        }
    }
}
