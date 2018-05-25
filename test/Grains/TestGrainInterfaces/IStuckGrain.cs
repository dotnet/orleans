using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStuckGrain : IGrainWithGuidKey
    {
        Task RunForever();

        Task NonBlockingCall();

        Task<int> GetNonBlockingCallCounter();

        Task BlockingDeactivation();
    }

    public interface IStuckCleanGrain : IGrainWithGuidKey
    {
        Task Release(Guid key);

        Task<bool> IsActivated(Guid key);
    }
}
