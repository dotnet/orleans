using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ReentrantTestSupportGrain : Grain, IReentrantTestSupportGrain
    {
        private readonly GrainTypeManager grainTypeManager;

        public ReentrantTestSupportGrain(GrainTypeManager grainTypeManager)
        {
            this.grainTypeManager = grainTypeManager;
        }

        public Task<bool> IsReentrant(string fullTypeName)
        {
            GrainTypeData data;
            this.grainTypeManager.TryGetData(fullTypeName, out data);
            return Task.FromResult(data.IsReentrant);
        }
    }
}
