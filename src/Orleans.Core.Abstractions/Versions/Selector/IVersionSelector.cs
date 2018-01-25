using System;
using System.Collections.Generic;
using Orleans.Versions.Compatibility;

namespace Orleans.Versions.Selector
{
    public interface IVersionSelector
    {
        IReadOnlyList<ushort> GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, ICompatibilityDirector compatibilityDirector);
    }

    [Serializable]
    public abstract class VersionSelectorStrategy
    {
    }
}