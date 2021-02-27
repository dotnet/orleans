using System;
using Orleans.Versions.Compatibility;

namespace Orleans.Versions.Selector
{
    public interface IVersionSelector
    {
        ushort[] GetSuitableVersion(ushort requestedVersion, ushort[] availableVersions, ICompatibilityDirector compatibilityDirector);
    }

    [Serializable]
    [GenerateSerializer]
    public abstract class VersionSelectorStrategy
    {
    }
}