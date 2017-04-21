using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Placement;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Placement;

namespace Orleans.Runtime.Versions
{
    internal class CachedVersionDirectorManager
    {
        private readonly GrainTypeManager grainTypeManager;
        private readonly Func<Tuple<int, int, ushort>, IReadOnlyList<SiloAddress>> getSilosFunc;
        private ConcurrentDictionary<Tuple<int,int,ushort>, IReadOnlyList<SiloAddress>> suitableSilosCache;

        public VersionPlacementDirectorManager VersionPlacementDirectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedVersionDirectorManager(GrainTypeManager grainTypeManager, VersionPlacementDirectorManager versionPlacementDirectorManager, CompatibilityDirectorManager compatibilityDirectorManager)
        {
            this.grainTypeManager = grainTypeManager;
            this.VersionPlacementDirectorManager = versionPlacementDirectorManager;
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

            var placementDirector = this.VersionPlacementDirectorManager.GetDirector(ifaceId);
            var compatibilityDirector = this.CompatibilityDirectorManager.GetDirector(ifaceId);
            var versions = placementDirector.GetSuitableVersion(
                requestedVersion, 
                this.grainTypeManager.GetAvailableVersions(ifaceId), 
                compatibilityDirector);

            return this.grainTypeManager.GetSupportedSilos(typeCode, ifaceId, versions);
        }
    }
}