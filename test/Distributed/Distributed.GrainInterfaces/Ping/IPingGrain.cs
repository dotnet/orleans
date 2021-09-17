using Orleans;
using System.Threading.Tasks;

namespace Distributed.GrainInterfaces.Ping
{
    public interface IPingGrain : IGrainWithGuidKey
    {
        Task Ping();
    }
}
