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
        /// Gets an <see cref="IGrainDirectory"/> instance for the provided <see cref="GrainType"/>.
        /// </summary>
        bool TryResolveGrainDirectory(GrainType grainType, GrainProperties properties, out IGrainDirectory grainDirectory);
    }
}
