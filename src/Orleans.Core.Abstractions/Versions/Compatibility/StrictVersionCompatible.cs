using System;

namespace Orleans.Versions.Compatibility
{
    /// <summary>
    /// A grain interface version compatibility strategy which treats all versions of an interface compatible only with equal requested versions.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class StrictVersionCompatible : CompatibilityStrategy
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static StrictVersionCompatible Singleton { get; } = new StrictVersionCompatible();
    }
}