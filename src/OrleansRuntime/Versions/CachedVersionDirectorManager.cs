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

        public VersionPlacementDirectorManager VersionPlacementDirectorManager { get; }

        public CompatibilityDirectorManager CompatibilityDirectorManager { get; }

        public CachedVersionDirectorManager(GrainTypeManager grainTypeManager, VersionPlacementDirectorManager versionPlacementDirectorManager, CompatibilityDirectorManager compatibilityDirectorManager)
        {
            this.grainTypeManager = grainTypeManager;
            this.VersionPlacementDirectorManager = versionPlacementDirectorManager;
            this.CompatibilityDirectorManager = compatibilityDirectorManager;
            this.directors = new ConcurrentDictionary<int, CachedVersionDirector>();
        }

        public IReadOnlyList<ushort> GetSuitableVersion(int ifaceId, ushort requestedVersion)
        {
            var director = this.directors.GetOrAdd(ifaceId, GetDirector);
            return director.GetSuitableVersion(requestedVersion);
        }

        public void ResetCache()
        {
            this.directors = new ConcurrentDictionary<int, CachedVersionDirector>();
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