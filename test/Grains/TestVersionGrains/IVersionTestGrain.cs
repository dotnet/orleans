using Orleans.CodeGeneration;

namespace TestVersionGrainInterfaces
{
#if VERSION_1
    [Version(1)]
#else
    [Version(2)]
#endif
    public interface IVersionUpgradeTestGrain : IGrainWithIntegerKey
    {
        Task<int> GetVersion();

        Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other);

        Task<bool> LongRunningTask(TimeSpan taskTime);
    }

#if VERSION_1
    [Version(1)]
#else
    [Version(2)]
#endif
    public interface IVersionPlacementTestGrain : IGrainWithIntegerKey
    {
        Task<int> GetVersion();
    }
}
