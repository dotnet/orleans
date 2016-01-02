using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class KeyExtensionTestGrain : Grain, IKeyExtensionTestGrain
    {
        public Task<IKeyExtensionTestGrain> GetGrainReference()
        {
            return Task.FromResult(this.AsReference<IKeyExtensionTestGrain>());
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }
    }
}
