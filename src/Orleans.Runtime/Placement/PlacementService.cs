using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Versions;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// Central point for placement decisions.
    /// </summary>
    internal class PlacementService : IPlacementContext
    {
        private readonly PlacementStrategyResolver _strategyResolver;
        private readonly PlacementDirectorResolver _directorResolver;
        private readonly ILogger<PlacementService> _logger;
        private readonly GrainLocator _grainLocator;
        private readonly GrainVersionManifest _grainInterfaceVersions;
        private readonly CachedVersionSelectorManager _versionSelectorManager;
        private readonly ISiloStatusOracle _siloStatusOracle;
        private readonly bool _assumeHomogeneousSilosForTesting;

        /// <summary>
        /// Create a <see cref="PlacementService"/> instance.
        /// </summary>
        public PlacementService(
            IOptionsMonitor<SiloMessagingOptions> siloMessagingOptions,
            ILocalSiloDetails localSiloDetails,
            ISiloStatusOracle siloStatusOracle,
            ILogger<PlacementService> logger,
            GrainLocator grainLocator,
            GrainVersionManifest grainInterfaceVersions,
            CachedVersionSelectorManager versionSelectorManager,
            PlacementDirectorResolver directorResolver,
            PlacementStrategyResolver strategyResolver)
        {
            LocalSilo = localSiloDetails.SiloAddress;
            _strategyResolver = strategyResolver;
            _directorResolver = directorResolver;
            _logger = logger;
            _grainLocator = grainLocator;
            _grainInterfaceVersions = grainInterfaceVersions;
            _versionSelectorManager = versionSelectorManager;
            _siloStatusOracle = siloStatusOracle;
            _assumeHomogeneousSilosForTesting = siloMessagingOptions.CurrentValue.AssumeHomogenousSilosForTesting;
        }

        public SiloAddress LocalSilo { get; }

        public SiloStatus LocalSiloStatus => _siloStatusOracle.CurrentStatus;

        /// <summary>
        /// Gets or places an activation.
        /// </summary>
        public Task AddressMessage(Message message)
        {
            if (message.IsFullyAddressed) return Task.CompletedTask;
            if (message.TargetGrain.IsDefault) ThrowMissingAddress();

            var grainId = message.TargetGrain;
            if (_grainLocator.TryLocalLookup(grainId, out var result))
            {
                SetMessageTargetPlacement(message, result.Activation, result.Silo, false);
                return Task.CompletedTask;
            }

            return GetOrPlaceActivationAsync(message);

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void ThrowMissingAddress() => throw new InvalidOperationException("Cannot address a message without a target");
        }

        private async Task GetOrPlaceActivationAsync(Message message)
        {
            var target = new PlacementTarget(
                message.TargetGrain,
                message.RequestContextData,
                message.InterfaceType,
                message.InterfaceVersion);

            var targetGrain = target.GrainIdentity;
            var result = await _grainLocator.Lookup(targetGrain);
            if (result is not null)
            {
                SetMessageTargetPlacement(message, result.Activation, result.Silo, false);
                return;
            }

            var strategy = _strategyResolver.GetPlacementStrategy(target.GrainIdentity.Type);
            var director = _directorResolver.GetPlacementDirector(strategy);
            var siloAddress = await director.OnAddActivation(strategy, target, this);

            ActivationId activationId;
            if (strategy.IsDeterministicActivationId)
            {
                // Use the grain id as the activation id.
                activationId = ActivationId.GetDeterministic(target.GrainIdentity);
            }
            else
            {
                activationId = ActivationId.NewId();
            }

            SetMessageTargetPlacement(message, activationId, siloAddress, true);
        }

        private void SetMessageTargetPlacement(Message message, ActivationId activationId, SiloAddress targetSilo, bool isNewPlacement)
        {
            message.TargetActivation = activationId;
            message.TargetSilo = targetSilo;
            message.IsNewPlacement = isNewPlacement;
            if (isNewPlacement)
            {
                CounterStatistic.FindOrCreate(StatisticNames.DISPATCHER_NEW_PLACEMENT).Increment();
            }
#if DEBUG
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.Trace(ErrorCode.Dispatcher_AddressMsg_SelectTarget, "AddressMessage Placement SelectTarget {0}", message);
#endif
        }

        public SiloAddress[] GetCompatibleSilos(PlacementTarget target)
        {
            // For test only: if we have silos that are not yet in the Cluster TypeMap, we assume that they are compatible
            // with the current silo
            if (_assumeHomogeneousSilosForTesting)
            {
                return AllActiveSilos;
            }

            var grainType = target.GrainIdentity.Type;
            var silos = target.InterfaceVersion > 0
                ? _versionSelectorManager.GetSuitableSilos(grainType, target.InterfaceType, target.InterfaceVersion).SuitableSilos
                : _grainInterfaceVersions.GetSupportedSilos(grainType).Result;

            var compatibleSilos = silos.Intersect(AllActiveSilos).ToArray();
            if (compatibleSilos.Length == 0)
            {
                var allWithType = _grainInterfaceVersions.GetSupportedSilos(grainType).Result;
                var versions = _grainInterfaceVersions.GetSupportedSilos(target.InterfaceType, target.InterfaceVersion).Result;
                var allWithTypeString = string.Join(", ", allWithType.Select(s => s.ToString())) is string withGrain && !string.IsNullOrWhiteSpace(withGrain) ? withGrain : "none";
                var allWithInterfaceString = string.Join(", ", versions.Select(s => s.ToString())) is string withIface && !string.IsNullOrWhiteSpace(withIface) ? withIface : "none";
                throw new OrleansException(
                    $"No active nodes are compatible with grain {grainType} and interface {target.InterfaceType} version {target.InterfaceVersion}. "
                    + $"Known nodes with grain type: {allWithTypeString}. "
                    + $"All known nodes compatible with interface version: {allWithTypeString}");
            }

            return compatibleSilos;
        }

        public SiloAddress[] AllActiveSilos
        {
            get
            {
                var result = _siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToArray();
                if (result.Length > 0) return result;

                _logger.Warn(ErrorCode.Catalog_GetApproximateSiloStatuses, "AllActiveSilos SiloStatusOracle.GetApproximateSiloStatuses empty");
                return new SiloAddress[] { LocalSilo };
            }
        }

        public IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target)
        {
            if (target.InterfaceVersion == 0)
            {
                throw new ArgumentException("Interface version not provided", nameof(target));
            }

            var grainType = target.GrainIdentity.Type;
            var silos = _versionSelectorManager
                .GetSuitableSilos(grainType, target.InterfaceType, target.InterfaceVersion)
                .SuitableSilosByVersion;

            return silos;
        }
    }
}
