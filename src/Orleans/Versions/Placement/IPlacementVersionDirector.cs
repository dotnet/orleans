using Orleans.Versions.Compatibility;
using System;
using System.Collections.Generic;

namespace Orleans.Versions.Placement
{
    public interface IVersionPlacementDirector
    {
        ushort GetSuitableVersion(ushort requestedVersion, IReadOnlyList<ushort> availableVersions, IVersionCompatibilityDirector versionCompatibilityDirector);
    }

    public interface IVersionPlacementDirector<TPolicy> : IVersionPlacementDirector where TPolicy : VersionPlacementStrategy
    {
    }

    [Serializable]
    public abstract class VersionPlacementStrategy
    {
    }
}