using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IMultipleImplicitSubscriptionGrain : IGrainWithGuidKey
    {
        Task<Tuple<int, int>> GetCounters();
    }
}
