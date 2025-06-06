using Microsoft.Extensions.Logging;
using Orleans.Placement;
using TestVersionGrainInterfaces;
using UnitTests.GrainInterfaces;

namespace TestVersionGrains
{
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
