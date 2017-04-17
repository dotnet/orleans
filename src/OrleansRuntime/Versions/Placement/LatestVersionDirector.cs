using System;
using System.Linq;
using Orleans.Versions.Placement;
using System.Collections.Generic;
using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Placement
{
    internal sealed class LatestVersionPlacementDirector : IVersionPlacementDirector<LatestVersionPlacement>
    {
        public IReadOnlyList<ushort> GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, IVersionCompatibilityDirector versionCompatibilityDirector)
        {
            return new[]
            {
                availableVersions.Where(v => versionCompatibilityDirector.IsCompatible(requestedVersion, v)).Max()
            };
        }
    }
}
