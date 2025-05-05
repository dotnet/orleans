using System;

namespace Orleans.Runtime.Advanced
{
    /// <summary>
    /// Type manager for advanced grain usage scenarios.
    /// </summary>
    public interface IGrainTypeResolver
    {
        /// <summary>
        /// Returns the grain type for the provided class identified by <paramref name="type"/>.
        /// </summary>
        string GetGrainType(Type type);
    }
}
