using System.Collections.Generic;
using System.Linq;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal class AllCompatibleVersionsSelector : IVersionSelector<AllCompatibleVersions>
    {
        public IReadOnlyList<ushort> GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, ICompatibilityDirector compatibilityDirector)
        {
            return availableVersions.Where(v => compatibilityDirector.IsCompatible(requestedVersion, v)).ToList();
        }
    }
}
