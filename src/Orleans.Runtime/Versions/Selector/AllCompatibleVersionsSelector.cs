using System.Linq;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal class AllCompatibleVersionsSelector : IVersionSelector
    {
        public ushort[] GetSuitableVersion(ushort requestedVersion, ushort[] availableVersions, ICompatibilityDirector compatibilityDirector)
        {
            return availableVersions.Where(v => compatibilityDirector.IsCompatible(requestedVersion, v)).ToArray();
        }
    }
}
