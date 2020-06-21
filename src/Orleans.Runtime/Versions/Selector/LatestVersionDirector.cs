using System;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal sealed class LatestVersionSelector : IVersionSelector
    {
        public ushort[] GetSuitableVersion(ushort requestedVersion, ushort[] availableVersions, ICompatibilityDirector compatibilityDirector)
        {
            var max = int.MinValue;
            foreach (var version in availableVersions)
            {
                if (compatibilityDirector.IsCompatible(requestedVersion, version) && version > max)
                {
                    max = version;
                }
            }

            if (max < 0) return Array.Empty<ushort>();

            return new[] { (ushort)max };
        }
    }
}
