using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;

namespace TestVersionGrainInterfaces
{
    [Version(2)]
    public interface IVersionUpgradeTestGrain : IGrainWithIntegerKey
    {
        Task<int> GetVersion();

        Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other);

        Task<bool> LongRunningTask(TimeSpan taskTime);
    }

    [Version(2)]
    public interface IVersionPlacementTestGrain : IGrainWithIntegerKey
    {
        Task<int> GetVersion();
    }
}
