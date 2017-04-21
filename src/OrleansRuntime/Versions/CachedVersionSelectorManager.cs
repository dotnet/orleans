using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;

namespace Orleans.Runtime.Versions
{
    internal class CachedVersionSelectorManager
    {
        private readonly GrainTypeManager grainTypeManager;
        private readonly Func<Tuple<int, int, ushort>, IReadOnlyList<SiloAddress>> getSilosFunc;
        private ConcurrentDictionary<Tuple<int,int,ushort>, IReadOnlyList<SiloAddress>> suitableSilosCache;

        public VersionSelectorManager VersionSelectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedVersionSelectorManager(GrainTypeManager grainTypeManager, VersionSelectorManager versionSelectorManager, CompatibilityDirectorManager compatibilityDirectorManager)
        {
            this.grainTypeManager = grainTypeManager;
            this.VersionSelectorManager = versionSelectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.getSilosFunc = GetSuitableSilosImpl;
        }

        public IReadOnlyList<SiloAddress> GetSuitableSilos(int typeCode, int ifaceId, ushort requestedVersion)
        {
            var key = Tuple.Create(typeCode, ifaceId, requestedVersion);
            return suitableSilosCache.GetOrAdd(key, getSilosFunc);
        }

        public void ResetCache()
        {
            this.suitableSilosCache = new ConcurrentDictionary<Tuple<int, int, ushort>, IReadOnlyList<SiloAddress>>();
        }

        private IReadOnlyList<SiloAddress> GetSuitableSilosImpl(Tuple<int, int, ushort> key)
        {
            var typeCode = key.Item1;
            var ifaceId = key.Item2;
            var requestedVersion = key.Item3;

            var placementDirector = this.VersionSelectorManager.GetSelector(ifaceId);
            var compatibilityDirector = this.CompatibilityDirectorManager.GetDirector(ifaceId);
            var versions = placementDirector.GetSuitableVersion(
                requestedVersion, 
                this.grainTypeManager.GetAvailableVersions(ifaceId), 
                compatibilityDirector);

            return this.grainTypeManager.GetSupportedSilos(typeCode, ifaceId, versions);
        }
    }
}