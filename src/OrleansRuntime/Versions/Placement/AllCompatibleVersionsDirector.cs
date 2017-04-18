using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Placement;

namespace Orleans.Runtime.Versions.Placement
{
    internal class AllCompatibleVersionsPlacementDirector : IVersionPlacementDirector<AllCompatibleVersions>
    {
        public IReadOnlyList<ushort> GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, IVersionCompatibilityDirector versionCompatibilityDirector)
        {
            return availableVersions.Where(v => versionCompatibilityDirector.IsCompatible(requestedVersion, v)).ToList();
        }
    }
}
