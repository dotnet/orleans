using Orleans;
using Orleans.Runtime;
using System;
using System.Threading.Tasks;

namespace Distributed.GrainInterfaces.Ping
{
    public interface IPingRouterGrain : IGrainWithGuidKey
    {
        Task Ping(params Guid[] grainIds);
    }
}
