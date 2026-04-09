using System;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal sealed class LatestVersionSelector : IVersionSelector
    {
        public GrainInterfaceVersion[] GetSuitableVersion(GrainInterfaceVersion requestedVersion, GrainInterfaceVersion[] availableVersions, ICompatibilityDirector compatibilityDirector)
        {
            GrainInterfaceVersion? max = null;
            foreach (var version in availableVersions)
            {
                if (compatibilityDirector.IsCompatible(requestedVersion, version) && (max is null || version > max))
                {
                    max = version;
                }
            }

            if (max is null) return Array.Empty<GrainInterfaceVersion>();

            return new[] { max.Value };
        }
    }
}
