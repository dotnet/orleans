using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces.Placement
{
    public interface ISiloServicePlacementGrain : IGrainWithStringKey
    {
        Task<SiloAddress> GetSilo();
        Task<string> GetKey();
    }
}
