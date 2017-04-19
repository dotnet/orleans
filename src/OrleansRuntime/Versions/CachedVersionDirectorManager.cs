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
        private ConcurrentDictionary<int, CachedVersionDirector> directors;
        private readonly Func<int, CachedVersionDirector> getDirectorFunc;
        private readonly Func<Tuple<int, int, ushort>, IReadOnlyList<SiloAddress>> getSilosFunc;
        private ConcurrentDictionary<Tuple<int,int,ushort>, IReadOnlyList<SiloAddress>> suitableSilosCache;

        public VersionPlacementDirectorManager VersionPlacementDirectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedVersionDirectorManager(GrainTypeManager grainTypeManager, VersionPlacementDirectorManager versionPlacementDirectorManager, CompatibilityDirectorManager compatibilityDirectorManager)
        {
            this.grainTypeManager = grainTypeManager;
            this.VersionPlacementDirectorManager = versionPlacementDirectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.directors = new ConcurrentDictionary<int, CachedVersionDirector>();
            this.getDirectorFunc = GetDirector;
            this.getSilosFunc = GetSuitableSilosImpl;
        }

        public IReadOnlyList<SiloAddress> GetSuitableSilos(int typeCode, int ifaceId, ushort requestedVersion)
        {
            var key = Tuple.Create(typeCode, ifaceId, requestedVersion);
            return suitableSilosCache.GetOrAdd(key, getSilosFunc);
        }

        public void ResetCache()
        {
            this.directors = new ConcurrentDictionary<int, CachedVersionDirector>();
            this.suitableSilosCache = new ConcurrentDictionary<Tuple<int, int, ushort>, IReadOnlyList<SiloAddress>>();
        }

        private IReadOnlyList<SiloAddress> GetSuitableSilosImpl(Tuple<int, int, ushort> key)
        {
            var typeCode = key.Item1;
            var ifaceId = key.Item2;
            var requestedVersion = key.Item3;
            var director = this.directors.GetOrAdd(ifaceId, getDirectorFunc);
            var versions = director.GetSuitableVersion(requestedVersion);
            return this.grainTypeManager.GetSupportedSilos(typeCode, ifaceId, versions);
        }

        private CachedVersionDirector GetDirector(int ifaceId)
        {
            var version = this.VersionPlacementDirectorManager.GetDirector(ifaceId);
            var compat = this.CompatibilityDirectorManager.GetDirector(ifaceId);
            var availableVersions = this.grainTypeManager.GetAvailableVersions(ifaceId);

            return new CachedVersionDirector(version, compat, availableVersions);
        }
    }
}