using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Placement;

namespace Orleans.Runtime.Versions
{
    internal class CachedVersionDirector
    {
        private readonly IVersionPlacementDirector versionPlacement;
        private readonly IVersionCompatibilityDirector versionCompatibility;
        private readonly IReadOnlyList<ushort> availableVersions;
        private readonly ConcurrentDictionary<ushort, ushort> cachedResults;

        public CachedVersionDirector(IVersionPlacementDirector versionPlacement, IVersionCompatibilityDirector versionCompatibility, IReadOnlyList<ushort> availableVersions)
        {
            this.versionPlacement = versionPlacement;
            this.versionCompatibility = versionCompatibility;
            this.availableVersions = availableVersions;
            this.cachedResults = new ConcurrentDictionary<ushort, ushort>();
        }

        public ushort GetSuitableVersion(ushort requestedVersion)
        {
            return cachedResults.GetOrAdd(requestedVersion, GetSuitableVersionImpl);
        }

        private ushort GetSuitableVersionImpl(ushort requestedVersion)
        {
            return this.versionPlacement.GetSuitableVersion(requestedVersion, this.availableVersions, this.versionCompatibility);
        }
    }
}