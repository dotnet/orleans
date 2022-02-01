using System;
using Orleans.Versions.Compatibility;

namespace Orleans.Versions.Selector
{
    /// <summary>
    /// Functionality for selecting which versions of a grain interface should be preferred when performing grain placement.
    /// </summary>
    public interface IVersionSelector
    {
        /// <summary>
        /// Returns a collection of suitable interface versions for a given request.
        /// </summary>
        /// <param name="requestedVersion">The requested grain interface version.</param>
        /// <param name="availableVersions">The collection of available interface versions.</param>
        /// <param name="compatibilityDirector">The compatibility director.</param>
        /// <returns>A collection of suitable interface versions for a given request.</returns>
        ushort[] GetSuitableVersion(ushort requestedVersion, ushort[] availableVersions, ICompatibilityDirector compatibilityDirector);
    }

    /// <summary>
    /// Base class for all grain interface version selector strategies.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public abstract class VersionSelectorStrategy
    {
    }
}