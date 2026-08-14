using System;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<(GrainType Type, GrainInterfaceType Interface, ushort Version), CachedEntry> suitableSilosCache;
        private readonly GrainVersionManifest grainInterfaceVersions;
        private readonly IClusterMembershipService clusterMembershipService;
        private long cacheGeneration;

        public CachedVersionSelectorManager(
            GrainVersionManifest grainInterfaceVersions,
            VersionSelectorManager versionSelectorManager,
            CompatibilityDirectorManager compatibilityDirectorManager,
            IClusterMembershipService clusterMembershipService)
        {
            this.grainInterfaceVersions = grainInterfaceVersions;
            this.VersionSelectorManager = versionSelectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.clusterMembershipService = clusterMembershipService;
            this.suitableSilosCache = new ConcurrentDictionary<(GrainType Type, GrainInterfaceType Interface, ushort Version), CachedEntry>();
        }

        public VersionSelectorManager VersionSelectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedEntry GetSuitableSilos(GrainType grainType, GrainInterfaceType interfaceId, ushort requestedVersion)
        {
            var key = ValueTuple.Create(grainType, interfaceId, requestedVersion);
            while (true)
            {
                var generation = Volatile.Read(ref this.cacheGeneration);
                var membershipVersion = this.clusterMembershipService.CurrentSnapshot.Version;
                var manifestVersion = this.grainInterfaceVersions.LatestVersion;
                if (suitableSilosCache.TryGetValue(key, out var entry)
                    && entry.Generation == generation
                    && entry.Version == manifestVersion
                    && entry.Version.Major == membershipVersion.Value
                    && VersionsAreCurrent(generation, membershipVersion, manifestVersion))
                {
                    return entry;
                }

                entry = GetSuitableSilosImpl(key, generation);
                if (entry.Version == manifestVersion
                    && entry.Version.Major == membershipVersion.Value
                    && VersionsAreCurrent(generation, membershipVersion, manifestVersion))
                {
                    return suitableSilosCache[key] = entry;
                }
            }
        }

        public SiloAddress[] GetSupportedSilos(GrainType grainType)
        {
            while (true)
            {
                var membershipVersion = this.clusterMembershipService.CurrentSnapshot.Version;
                var manifestVersion = this.grainInterfaceVersions.LatestVersion;
                var (resultVersion, result) = this.grainInterfaceVersions.GetSupportedSilos(grainType);
                if (resultVersion == manifestVersion
                    && resultVersion.Major == membershipVersion.Value
                    && VersionsAreCurrent(membershipVersion, manifestVersion))
                {
                    return result;
                }
            }
        }

        public void ResetCache()
        {
            Interlocked.Increment(ref this.cacheGeneration);
            this.suitableSilosCache.Clear();
        }

        private CachedEntry GetSuitableSilosImpl(
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

            return new CachedEntry
            {
                Generation = generation,
                Version = version,
                SuitableSilos = result.SelectMany(sv => sv.Value).Distinct().OrderBy(addr => addr).ToArray(),
                SuitableSilosByVersion = result,
            };
        }

        private bool VersionsAreCurrent(long generation, MembershipVersion membershipVersion, MajorMinorVersion manifestVersion) =>
            Volatile.Read(ref this.cacheGeneration) == generation
            && VersionsAreCurrent(membershipVersion, manifestVersion);

        private bool VersionsAreCurrent(MembershipVersion membershipVersion, MajorMinorVersion manifestVersion) =>
            this.grainInterfaceVersions.LatestVersion == manifestVersion
            && this.clusterMembershipService.CurrentSnapshot.Version == membershipVersion;

        internal struct CachedEntry
        {
            public long Generation { get; set; }

            public MajorMinorVersion Version { get; set; }

            public SiloAddress[] SuitableSilos { get; set; }

            public Dictionary<ushort, SiloAddress[]> SuitableSilosByVersion { get; set; }
        }
    }
}