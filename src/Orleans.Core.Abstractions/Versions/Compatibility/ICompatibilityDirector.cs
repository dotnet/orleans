using System;

namespace Orleans.Versions.Compatibility
{
    /// <summary>
    /// Functionality for grain interface compatibility directors.
    /// </summary>
    public interface ICompatibilityDirector
    {
        /// <summary>
        /// Returns <see langword="true"/> if the current version of an interface is compatible with the requested version, <see langword="false"/> otherwise.
        /// </summary>
        /// <param name="requestedVersion">The requested interface version.</param>
        /// <param name="currentVersion">The currently available interface version.</param>
        /// <returns><see langword="true"/> if the current version of an interface is compatible with the requested version, <see langword="false"/> otherwise.</returns>
        bool IsCompatible(ushort requestedVersion, ushort currentVersion);
    }

    /// <summary>
    /// Base class for all grain interface version compatibility strategies.
    /// </summary>
    [Serializable, SerializerTransparent]
    public abstract class CompatibilityStrategy
    {
    }
}