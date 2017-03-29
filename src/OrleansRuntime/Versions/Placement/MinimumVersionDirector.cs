using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Placement;

namespace Orleans.Runtime.Versions.Placement
{
    internal sealed class MinimumVersionPlacementDirector : IVersionPlacementDirector<MinimumVersionPlacement>
    {
        public ushort GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, IVersionCompatibilityDirector versionCompatibilityDirector)
        {
            return availableVersions.Where(v => versionCompatibilityDirector.IsCompatible(requestedVersion, v)).Min();
        }
    }
}