using System.Collections.Generic;

namespace Orleans.Metadata
{
    /// <summary>
    /// Contains grain interface descriptions.
    /// </summary>
    public class GrainInterfaceFeature
    {
        /// <summary>
        /// Gets a collection of metadata about grain interfaces.
        /// </summary>
        public IList<GrainInterfaceMetadata> Interfaces { get; } = new List<GrainInterfaceMetadata>();
    }
}