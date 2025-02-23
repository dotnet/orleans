using Orleans.Metadata;
using Orleans.Placement;
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
    // We override equality and hashcode as this type is used as the dictionary key,
    // and record structs use default equality comparer, which for an enum is not that great for performance.
    private readonly record struct StatusKey(GrainType Type, ImmovableKind Kind) : IEquatable<StatusKey>
    {
        public bool Equals(StatusKey other) => Type == other.Type && Kind == other.Kind;
        public override int GetHashCode() => HashCode.Combine(Type.GetUniformHashCode(), Kind);
    }

    private readonly GrainManifest _localManifest = clusterManifestProvider.LocalGrainManifest;
    private readonly PlacementStrategyResolver _strategyResolver = strategyResolver;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ConcurrentDictionary<StatusKey, bool> _migratableStatuses = new();
    private FrozenDictionary<StatusKey, bool>? _migratableStatusesCache;
    private long _lastRegeneratedCacheTimestamp = timeProvider.GetTimestamp();

    public bool IsMigratable(GrainType grainType, ImmovableKind expectedKind)
    {
        var statusKey = new StatusKey(grainType, expectedKind);
        if (_migratableStatusesCache is { } cache && cache.TryGetValue(statusKey, out var isMigratable))
        {
            return isMigratable;
        }

        return IsMigratableRare(grainType, statusKey);

        bool IsMigratableRare(GrainType grainType, StatusKey statusKey)
        {
            // _migratableStatuses holds statuses for each grain type if its migratable type or not, so we can make fast lookups.
            // since we don't anticipate a huge number of grain *types*, i think its just fine to have this in place as fast-check.
            if (!_migratableStatuses.TryGetValue(statusKey, out var isMigratable))
            {
                isMigratable = !(grainType.IsClient() || grainType.IsSystemTarget() || grainType.IsGrainService() || IsStatelessWorker(grainType) || IsImmovable(grainType));
                _migratableStatuses.TryAdd(statusKey, isMigratable);
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
                if (!props.Properties.TryGetValue(WellKnownGrainTypeProperties.Immovable, out var value))
                {
                    return false;
                }

                // If the value fails to parse, assume it's immovable.
                if (!byte.TryParse(value, out var actualKindValue))
                {
                    return true;
                }

                // It is immovable, but does the kind match with the parameter.
                return ((ImmovableKind)actualKindValue & expectedKind) == expectedKind;
            }

            // Assume unknown grains are immovable.
            return true;
        }
    }
}
