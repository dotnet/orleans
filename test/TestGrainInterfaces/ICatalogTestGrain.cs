using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ICatalogTestGrain : IGrainWithStringKey
    {
        Task Initialize();
        Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach);
    }
}
