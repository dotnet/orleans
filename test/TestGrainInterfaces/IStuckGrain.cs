using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStuckGrain : IGrainWithGuidKey
    {
        Task RunForever();
    }

    public interface IStuckCleanGrain : IGrainWithGuidKey
    {
        Task Release(Guid key);
        Task<bool> IsActivated(Guid key);
    }
}
