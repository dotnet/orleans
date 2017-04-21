using System.Collections.Generic;
using System.Linq;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal sealed class LatestVersionSelector : IVersionSelector<LatestVersion>
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
