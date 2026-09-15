using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Placement;
using Orleans.Serialization.Invocation;
using TestVersionGrainInterfaces;
using UnitTests.GrainInterfaces;

namespace TestVersionGrains
{
    [MayInterleave(nameof(MayInterleave))]
    [RandomPlacement]
    public class VersionUpgradeTestGrain : Grain, IVersionUpgradeTestGrain
    {
        private const int Version =
#if VERSION_1
            1;
#else
            2;
#endif

        private readonly ILogger _logger;

        public VersionUpgradeTestGrain(ILogger<VersionUpgradeTestGrain> logger)
        {
            logger.LogInformation("Creating version '{Version}'.", Version);
            _logger = logger;
        }

        public Task<int> GetVersion()
        {
            _logger.LogInformation("Version '{Version}' {GrainId} responding to GetVersion().", Version, this.GetGrainId());
            return Task.FromResult(Version);
        }

        public async Task<int> ProxyGetVersion(IVersionUpgradeTestGrain other)
        {
            _logger.LogInformation("Version '{Version}' {GrainId} calling {OtherGrainId}.", Version, this.GetGrainId(), other.GetGrainId());
            var otherVersion = await other.GetVersion();
            _logger.LogInformation("{OtherGrainId} returned '{OtherVersion}'.", other.GetGrainId(), otherVersion);
            return otherVersion;
        }

        public Task<int> ProxyCallVersion2Method(IVersionUpgradeTestGrain other)
        {
#if VERSION_1
            throw new InvalidOperationException("Version 2 is required to call the version 2 method.");
#else
            return other.Version2Method(new Version2Request { Value = Version });
#endif
        }

        public async Task<int> ProxyCallVersion2MethodAfterBarrier(IVersionUpgradeTestGrain other, IVersionUpgradeTestObserver observer)
        {
#if VERSION_1
            throw new InvalidOperationException("Version 2 is required to call the version 2 method.");
#else
            await observer.WaitForRelease();
            return await other.Version2Method(new Version2Request { Value = Version });
#endif
        }

        public async Task<int> ProxyCallCancellableVersion2MethodAfterBarrier(
            IVersionUpgradeTestGrain other,
            IVersionUpgradeTestObserver observer,
            CancellationToken cancellationToken)
        {
#if VERSION_1
            throw new InvalidOperationException("Version 2 is required to call the version 2 method.");
#else
            var call = other.CancellableVersion2Method(new Version2Request { Value = Version }, cancellationToken);
            await observer.WaitForRelease();
            return await call;
#endif
        }

        public Task WaitForRelease(IVersionUpgradeTestObserver observer) => observer.WaitForRelease();

        public async Task ProxyCallVersion2OneWayMethodAfterBarrier(
            IVersionUpgradeTestGrain other,
            IVersionUpgradeTestObserver barrier,
            IVersionUpgradeTestObserver deliveryObserver)
        {
#if VERSION_1
            throw new InvalidOperationException("Version 2 is required to call the version 2 method.");
#else
            await barrier.WaitForRelease();
            await other.Version2OneWayMethod(deliveryObserver);
#endif
        }

#if !VERSION_1
        public Task<int> Version2Method(Version2Request request) => Task.FromResult(request.Value);

        public Task<int> CancellableVersion2Method(Version2Request request, CancellationToken cancellationToken)
            => Task.FromResult(request.Value);

        public Task Version2OneWayMethod(IVersionUpgradeTestObserver observer) => observer.WaitForRelease();
#endif

        public static bool MayInterleave(IInvokable request) => false;

        public async Task<bool> LongRunningTask(TimeSpan taskTime)
        {
            await Task.Delay(taskTime);
            return true;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Activating version '{Version}'.", Version);
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Deactivating version '{Version}'.", Version);
            return base.OnDeactivateAsync(reason, cancellationToken);
        }
    }

    [VersionAwareStrategy]
    public class VersionPlacementTestGrain : Grain, IVersionPlacementTestGrain
    {
        private const int Version =
#if VERSION_1
            1;
#else
            2;
#endif

        private readonly ILogger _logger;

        public VersionPlacementTestGrain(ILogger<VersionPlacementTestGrain> logger)
        {
            logger.LogInformation("Creating version '{Version}'.", Version);
            _logger = logger;
        }

        public Task<int> GetVersion()
        {
            _logger.LogInformation("Version '{Version}' {GrainId} responding to GetVersion().", Version, this.GetGrainId());
            return Task.FromResult(Version);
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Activating version '{Version}'.", Version);
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Deactivating version '{Version}'.", Version);
            return base.OnDeactivateAsync(reason, cancellationToken);
        }
    }
}
