using System;

namespace Orleans.Runtime.Advanced
{
    /// <summary>
    /// Type manager for advanced interface usage scenarios.
    /// </summary>
    public interface IInterfaceTypeResolver
    {
        /// <summary>
        /// Returns the grain interface type name for the provided interface identified by <paramref name="type"/>.
        /// </summary>
        string GetGrainInterfaceType(Type type);
    }
}
