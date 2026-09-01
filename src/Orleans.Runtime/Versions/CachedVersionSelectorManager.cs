using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Orleans.Metadata;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;

namespace Orleans.Runtime.Versions
{
    internal sealed class CachedVersionSelectorManager
    {
        private readonly ConcurrentDictionary<(GrainType Type, GrainInterfaceType Interface, ushort Version), CacheSlot> suitableSilosCache;
        private readonly GrainVersionManifest grainInterfaceVersions;
        private long cacheGeneration;

        public CachedVersionSelectorManager(
            GrainVersionManifest grainInterfaceVersions,
            VersionSelectorManager versionSelectorManager,
            CompatibilityDirectorManager compatibilityDirectorManager)
        {
            this.grainInterfaceVersions = grainInterfaceVersions;
            this.VersionSelectorManager = versionSelectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.suitableSilosCache = new();
        }

        public VersionSelectorManager VersionSelectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedEntry GetSuitableSilos(GrainType grainType, GrainInterfaceType interfaceId, ushort requestedVersion)
        {
            var key = ValueTuple.Create(grainType, interfaceId, requestedVersion);
            var slot = suitableSilosCache.GetOrAdd(key, static _ => new());
            while (true)
            {
                var snapshot = this.grainInterfaceVersions.Capture();
                var generation = Volatile.Read(ref this.cacheGeneration);
                var value = Volatile.Read(ref slot.Value);
                if (value is not null
                    && value.CacheGeneration == generation
                    && value.ManifestVersion == snapshot.Version)
                {
                    return value.Entry;
                }

                lock (slot)
                {
                    snapshot = this.grainInterfaceVersions.Capture();
                    generation = Volatile.Read(ref this.cacheGeneration);
                    value = Volatile.Read(ref slot.Value);
                    if (value is not null
                        && value.CacheGeneration == generation
                        && value.ManifestVersion == snapshot.Version)
                    {
                        return value.Entry;
                    }

                    var entry = GetSuitableSilosImpl(snapshot, key);
                    if (generation == Volatile.Read(ref this.cacheGeneration))
                    {
                        Volatile.Write(ref slot.Value, new(generation, snapshot.Version, entry));
                    }

                    return entry;
                }
            }
        }

        public SiloAddress[] GetSupportedSilos(GrainType grainType) =>
            this.grainInterfaceVersions.Capture().GetSupportedSilos(grainType);

        public void ResetCache()
        {
            Interlocked.Increment(ref this.cacheGeneration);
            this.suitableSilosCache.Clear();
        }

        private CachedEntry GetSuitableSilosImpl(
            GrainVersionManifest.Snapshot snapshot,
            (GrainType Type, GrainInterfaceType Interface, ushort Version) key)
        {
            var grainType = key.Type;
            var interfaceType = key.Interface;
            var requestedVersion = key.Version;

            var versionSelector = this.VersionSelectorManager.GetSelector(interfaceType);
            var compatibilityDirector = this.CompatibilityDirectorManager.GetDirector(interfaceType);
            var available = snapshot.GetAvailableVersions(interfaceType);
            var versions = versionSelector.GetSuitableVersion(
                requestedVersion, 
                available, 
                compatibilityDirector);

            var result = snapshot.GetSupportedSilos(grainType, interfaceType, versions, out var suitableSilos);
            return new CachedEntry
            {
                SuitableSilos = suitableSilos,
                SuitableSilosByVersion = result,
            };
        }

        private sealed class CacheSlot
        {
            public CachedValue? Value;
        }

        private sealed record CachedValue(
            long CacheGeneration,
            MajorMinorVersion ManifestVersion,
            CachedEntry Entry);

        internal struct CachedEntry
        {
            public SiloAddress[] SuitableSilos { get; set; }

            public Dictionary<ushort, SiloAddress[]> SuitableSilosByVersion { get; set; }
        }
    }
}