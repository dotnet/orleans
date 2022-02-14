using System.Diagnostics.CodeAnalysis;
using Orleans.GrainDirectory;
using Orleans.Metadata;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Associates an <see cref="IGrainDirectory"/> instance with a <see cref="GrainType"/>.
    /// </summary>
    public interface IGrainDirectoryResolver
    {
        /// <summary>
        /// Gets an <see cref="IGrainDirectory" /> instance for the provided <see cref="GrainType" />.
        /// </summary>
        /// <param name="grainType">Type of the grain.</param>
        /// <param name="properties">The properties.</param>
        /// <param name="grainDirectory">The grain directory.</param>
        /// <returns>true if an appropriate grain directory was found, <see langword="false"/> otherwise.</returns>
        bool TryResolveGrainDirectory(GrainType grainType, GrainProperties properties, [NotNullWhen(true)] out IGrainDirectory grainDirectory);
    }
}
