using Orleans.CodeGeneration;
using Orleans.Concurrency;

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

        Task<int> ProxyCallVersion2Method(IVersionUpgradeTestGrain other);

        Task<int> ProxyCallVersion2MethodAfterBarrier(IVersionUpgradeTestGrain other, IVersionUpgradeTestObserver observer);

        Task<int> ProxyCallCancellableVersion2MethodAfterBarrier(
            IVersionUpgradeTestGrain other,
            IVersionUpgradeTestObserver observer,
            CancellationToken cancellationToken);

        Task ProxyCallVersion2OneWayMethodAfterBarrier(
            IVersionUpgradeTestGrain other,
            IVersionUpgradeTestObserver barrier,
            IVersionUpgradeTestObserver deliveryObserver);

        Task WaitForRelease(IVersionUpgradeTestObserver observer);

        Task<bool> LongRunningTask(TimeSpan taskTime);

#if !VERSION_1
        Task<int> Version2Method(Version2Request request);

        Task<int> CancellableVersion2Method(Version2Request request, CancellationToken cancellationToken);

        [OneWay]
        Task Version2OneWayMethod(IVersionUpgradeTestObserver observer);
#endif
    }

    public interface IVersionUpgradeTestObserver : IGrainObserver
    {
        Task WaitForRelease();
    }

#if !VERSION_1
    [GenerateSerializer]
    public sealed class Version2Request
    {
        [Id(0)]
        public int Value { get; set; }
    }
#endif

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
