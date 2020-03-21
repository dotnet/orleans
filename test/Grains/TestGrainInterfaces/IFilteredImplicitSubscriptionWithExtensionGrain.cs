using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IFilteredImplicitSubscriptionWithExtensionGrain : IGrainWithGuidCompoundKey
    {
        Task<int> GetCounter();
    }
}