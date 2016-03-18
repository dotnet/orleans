using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ReentrantTestSupportGrain : Grain, IReentrantTestSupportGrain
    {
        public Task<bool> IsReentrant(string fullTypeName)
        {
            GrainTypeData data;
            GrainTypeManager.Instance.TryGetData(fullTypeName, out data);
            return Task.FromResult(data.IsReentrant);
        }
    }
}
