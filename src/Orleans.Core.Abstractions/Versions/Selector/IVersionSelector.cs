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
        public static VersionSelectorStrategy Parse(string str)
        {
            if (str.Equals(typeof(AllCompatibleVersions).Name))
                return AllCompatibleVersions.Singleton;
            if (str.Equals(typeof(LatestVersion).Name))
                return LatestVersion.Singleton;
            if (str.Equals(typeof(MinimumVersion).Name))
                return MinimumVersion.Singleton;
            return null;
        }
    }
}