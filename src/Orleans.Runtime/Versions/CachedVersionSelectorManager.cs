using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Orleans.Metadata;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;

namespace Orleans.Runtime.Versions
{
    internal class CachedVersionSelectorManager
    {
#if NET9_0_OR_GREATER
        private readonly Lock cacheLock = new();
#else
        private readonly object cacheLock = new();
#endif
        private readonly Dictionary<(GrainType Type, GrainInterfaceType Interface, ushort Version), CachedEntry> suitableSilosCache;
        private readonly GrainVersionManifest grainInterfaceVersions;
        private readonly IClusterMembershipService? clusterMembershipService;
        private MajorMinorVersion observedManifestVersion;
        private long cacheGeneration;

        public CachedVersionSelectorManager(GrainVersionManifest grainInterfaceVersions, VersionSelectorManager versionSelectorManager, CompatibilityDirectorManager compatibilityDirectorManager)
            : this(grainInterfaceVersions, versionSelectorManager, compatibilityDirectorManager, clusterMembershipService: null)
        {
        }

        public CachedVersionSelectorManager(
            GrainVersionManifest grainInterfaceVersions,
            VersionSelectorManager versionSelectorManager,
            CompatibilityDirectorManager compatibilityDirectorManager,
            IClusterMembershipService? clusterMembershipService)
        {
            this.grainInterfaceVersions = grainInterfaceVersions;
            this.VersionSelectorManager = versionSelectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.clusterMembershipService = clusterMembershipService;
            this.observedManifestVersion = grainInterfaceVersions.LatestVersion;
            this.suitableSilosCache = new Dictionary<(GrainType Type, GrainInterfaceType Interface, ushort Version), CachedEntry>();
        }

        public VersionSelectorManager VersionSelectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public event Action? CacheInvalidated;

        public CachedEntry GetSuitableSilos(GrainType grainType, GrainInterfaceType interfaceId, ushort requestedVersion)
        {
            var key = ValueTuple.Create(grainType, interfaceId, requestedVersion);
            while (true)
            {
                CacheState state;
                lock (this.cacheLock)
                {
                    state = ObserveCacheState();
                    if (state.ManifestVersion.Major == state.MembershipVersion.Value
                        && suitableSilosCache.TryGetValue(key, out var cachedEntry)
                        && cachedEntry.Generation == state.Generation)
                    {
                        return cachedEntry;
                    }
                }

                var (resultVersion, entry) = GetSuitableSilosImpl(key, state.Generation);
                lock (this.cacheLock)
                {
                    var current = ObserveCacheState();
                    if (current.Generation == state.Generation
                        && resultVersion == current.ManifestVersion
                        && resultVersion.Major == current.MembershipVersion.Value)
                    {
                        return suitableSilosCache[key] = entry;
                    }
                }
            }
        }

        public SiloAddress[] GetSupportedSilos(GrainType grainType)
        {
            while (true)
            {
                CacheState state;
                lock (this.cacheLock)
                {
                    state = ObserveCacheState();
                }

                var (resultVersion, result) = this.grainInterfaceVersions.GetSupportedSilos(grainType);
                lock (this.cacheLock)
                {
                    var current = ObserveCacheState();
                    if (current.Generation == state.Generation
                        && resultVersion == current.ManifestVersion
                        && resultVersion.Major == current.MembershipVersion.Value)
                    {
                        return result;
                    }
                }
            }
        }

        public void ResetCache()
        {
            lock (this.cacheLock)
            {
                ++this.cacheGeneration;
            }

            CacheInvalidated?.Invoke();
        }

        private (MajorMinorVersion Version, CachedEntry Entry) GetSuitableSilosImpl(
            (GrainType Type, GrainInterfaceType Interface, ushort Version) key,
            long generation)
        {
            var grainType = key.Type;
            var interfaceType = key.Interface;
            var requestedVersion = key.Version;

            var versionSelector = this.VersionSelectorManager.GetSelector(interfaceType);
            var compatibilityDirector = this.CompatibilityDirectorManager.GetDirector(interfaceType);
            (var version, var available) = this.grainInterfaceVersions.GetAvailableVersions(interfaceType);
            var versions = versionSelector.GetSuitableVersion(
                requestedVersion, 
                available, 
                compatibilityDirector);

            (_, var result) = this.grainInterfaceVersions.GetSupportedSilos(grainType, interfaceType, versions);

            return (
                version,
                new CachedEntry
                {
                    Generation = generation,
                    SuitableSilos = result.SelectMany(sv => sv.Value).Distinct().OrderBy(addr => addr).ToArray(),
                    SuitableSilosByVersion = result,
                });
        }

        private CacheState ObserveCacheState()
        {
            var manifestVersion = this.grainInterfaceVersions.LatestVersion;
            var membershipVersion = this.clusterMembershipService?.CurrentSnapshot.Version
                ?? new MembershipVersion(manifestVersion.Major);
            if (manifestVersion != this.observedManifestVersion)
            {
                this.observedManifestVersion = manifestVersion;
                ++this.cacheGeneration;
            }

            return new CacheState(this.cacheGeneration, membershipVersion, manifestVersion);
        }

        private readonly record struct CacheState(
            long Generation,
            MembershipVersion MembershipVersion,
            MajorMinorVersion ManifestVersion);

        internal struct CachedEntry
        {
            public long Generation { get; set; }

            public SiloAddress[] SuitableSilos { get; set; }

            public Dictionary<ushort, SiloAddress[]> SuitableSilosByVersion { get; set; }
        }
    }
}