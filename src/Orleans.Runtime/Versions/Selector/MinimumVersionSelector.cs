using System.Linq;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal sealed class MinimumVersionSelector : IVersionSelector
    {
        public ushort[] GetSuitableVersion(ushort requestedVersion, ushort[] availableVersions, ICompatibilityDirector compatibilityDirector)
        {
            return new[]
            {
                availableVersions.Where(v => compatibilityDirector.IsCompatible(requestedVersion, v)).Min()
            };
        }
    }
}