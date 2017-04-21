using System;
using System.Collections.Generic;
using Orleans.Versions.Compatibility;

namespace Orleans.Versions.Selector
{
    public interface IVersionSelector
    {
        IReadOnlyList<ushort> GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, ICompatibilityDirector compatibilityDirector);
    }

    public interface IVersionSelector<TPolicy> : IVersionSelector where TPolicy : VersionSelectorStrategy
    {
    }

    [Serializable]
    public abstract class VersionSelectorStrategy
    {
    }
}