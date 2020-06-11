using System;
using System.Collections.Generic;
using Orleans.Versions.Compatibility;

namespace Orleans.Versions.Selector
{
    public interface IVersionSelector
    {
        ushort[] GetSuitableVersion(ushort requestedVersion, ushort[] availableVersions, ICompatibilityDirector compatibilityDirector);
    }

    [Serializable]
    public abstract class VersionSelectorStrategy
    {
    }
}