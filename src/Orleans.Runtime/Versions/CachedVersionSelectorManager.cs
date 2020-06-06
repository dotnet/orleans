using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;

namespace Orleans.Runtime.Versions
{
    internal class CachedVersionSelectorManager
    {
        private readonly ConcurrentDictionary<(GrainType Type, GrainInterfaceId Interface, ushort Version), CachedEntry> suitableSilosCache;
        private readonly GrainVersionManifest grainInterfaceVersions;

        public CachedVersionSelectorManager(GrainVersionManifest grainInterfaceVersions, VersionSelectorManager versionSelectorManager, CompatibilityDirectorManager compatibilityDirectorManager)
        {
            this.grainInterfaceVersions = grainInterfaceVersions;
            this.VersionSelectorManager = versionSelectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.suitableSilosCache = new ConcurrentDictionary<(GrainType Type, GrainInterfaceId Interface, ushort Version), CachedEntry>();
        }

        public VersionSelectorManager VersionSelectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedEntry GetSuitableSilos(GrainType grainType, GrainInterfaceId interfaceId, ushort requestedVersion)
        {
            var key = ValueTuple.Create(grainType, interfaceId, requestedVersion);
            if (!suitableSilosCache.TryGetValue(key, out var entry) || entry.Version < this.grainInterfaceVersions.LatestVersion)
            {
                entry = suitableSilosCache[key] = GetSuitableSilosImpl(key);
            }

            return entry;
        }

        public void ResetCache()
        {
            this.suitableSilosCache.Clear();
        }

        private CachedEntry GetSuitableSilosImpl((GrainType Type, GrainInterfaceId Interface, ushort Version) key)
        {
            var grainType = key.Type;
            var interfaceId = key.Interface;
            var requestedVersion = key.Version;

            var versionSelector = this.VersionSelectorManager.GetSelector(interfaceId);
            var compatibilityDirector = this.CompatibilityDirectorManager.GetDirector(interfaceId);
            (var version, var available) = this.grainInterfaceVersions.GetAvailableVersions(interfaceId);
            var versions = versionSelector.GetSuitableVersion(
                requestedVersion, 
                available, 
                compatibilityDirector);

            (_, var result) = this.grainInterfaceVersions.GetSupportedSilos(grainType, interfaceId, versions);

            return new CachedEntry
            {
                Version = version,
                SuitableSilos = result.SelectMany(sv => sv.Value).Distinct().OrderBy(addr => addr).ToArray(),
                SuitableSilosByVersion = result,
            };
        }

        internal struct CachedEntry
        {
            public MajorMinorVersion Version { get; set; }

            public SiloAddress[] SuitableSilos { get; set; }

            public Dictionary<ushort, SiloAddress[]> SuitableSilosByVersion { get; set; }
        }
    }
}