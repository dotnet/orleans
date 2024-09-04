using Orleans.Metadata;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

#nullable enable

namespace Orleans.Runtime.Placement;

internal sealed class GrainMigratabilityChecker(
    PlacementStrategyResolver strategyResolver,
    IClusterManifestProvider clusterManifestProvider,
    TimeProvider timeProvider)
{
    private readonly GrainManifest _localManifest = clusterManifestProvider.LocalGrainManifest;
    private readonly PlacementStrategyResolver _strategyResolver = strategyResolver;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ConcurrentDictionary<uint, bool> _migratableStatuses = new();
    private FrozenDictionary<uint, bool>? _migratableStatusesCache;
    private long _lastRegeneratedCacheTimestamp = timeProvider.GetTimestamp();

    public bool IsMigratable(GrainType grainType)
    {
        var hash = grainType.GetUniformHashCode();
        if (_migratableStatusesCache is { } cache && cache.TryGetValue(hash, out var isMigratable))
        {
            return isMigratable;
        }

        return IsMigratableRare(grainType, hash);

        bool IsMigratableRare(GrainType grainType, uint hash)
        {
            // _migratableStatuses holds statuses for each grain type if its migratable type or not, so we can make fast lookups.
            // since we don't anticipate a huge number of grain *types*, i think its just fine to have this in place as fast-check.
            if (!_migratableStatuses.TryGetValue(hash, out var isMigratable))
            {
                isMigratable = !(grainType.IsClient() || grainType.IsSystemTarget() || grainType.IsGrainService() || IsStatelessWorker(grainType) || IsImmovable(grainType));
                _migratableStatuses.TryAdd(hash, isMigratable);
            }

            // Regenerate the cache periodically.
            var currentTimestamp = _timeProvider.GetTimestamp();
            if (_timeProvider.GetElapsedTime(_lastRegeneratedCacheTimestamp, currentTimestamp) > TimeSpan.FromSeconds(5))
            {
                _migratableStatusesCache = _migratableStatuses.ToFrozenDictionary();
                _lastRegeneratedCacheTimestamp = currentTimestamp;
            }

            return isMigratable;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsStatelessWorker(GrainType grainType) =>
            _strategyResolver.GetPlacementStrategy(grainType).GetType() == typeof(StatelessWorkerPlacement);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsImmovable(GrainType grainType)
        {
            if (_localManifest.Grains.TryGetValue(grainType, out var props))
            {
                // If there is no 'Immovable' property, it is not immovable.
                // If the value fails to parse, assume it's immovable.
                // If the value is true, it's immovable.
                return props.Properties.TryGetValue(WellKnownGrainTypeProperties.Immovable, out var value) && (!bool.TryParse(value, out var result) || result);
            }

            // Assume unknown grains are immovable.
            return true;
        }
    }
}
