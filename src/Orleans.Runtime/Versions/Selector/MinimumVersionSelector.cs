using System.Linq;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal sealed class MinimumVersionSelector : IVersionSelector
    {
        public GrainInterfaceVersion[] GetSuitableVersion(GrainInterfaceVersion requestedVersion, GrainInterfaceVersion[] availableVersions, ICompatibilityDirector compatibilityDirector)
        {
            return new[]
            {
                availableVersions.Where(v => compatibilityDirector.IsCompatible(requestedVersion, v)).Min()
            };
        }
    }
}
