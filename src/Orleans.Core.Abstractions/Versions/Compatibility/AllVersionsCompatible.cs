using System;

namespace Orleans.Versions.Compatibility
{
    /// <summary>
    /// A grain interface version compatibility strategy which treats all versions of an interface compatible with any requested version.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class AllVersionsCompatible : CompatibilityStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static AllVersionsCompatible Singleton { get; } = new AllVersionsCompatible();
    }
}
