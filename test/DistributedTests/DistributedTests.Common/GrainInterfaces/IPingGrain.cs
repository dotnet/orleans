using Orleans;
using System.Threading.Tasks;

namespace DistributedTests.GrainInterfaces
{
    public interface IPingGrain : IGrainWithGuidKey
    {
        ValueTask Ping();
    }
}
