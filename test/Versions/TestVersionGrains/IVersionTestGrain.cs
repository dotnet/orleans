using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;

namespace TestVersionGrainInterfaces
{
    [Version(1)]
    public interface IVersionUpgradeTestGrain : IGrainWithIntegerKey
    {
        Task<int> GetVersion();

        Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other);

        Task<bool> LongRunningTask(TimeSpan taskTime);
    }
}
