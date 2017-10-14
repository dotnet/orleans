using System.Collections.Generic;

namespace Orleans.Metadata
{
    /// <summary>
    /// Contains grain class descriptions.
    /// </summary>
    public class GrainClassFeature
    {
        /// <summary>
        /// Gets a collection of metadata about grain classes.
        /// </summary>
        public IList<GrainClassMetadata> Classes { get; } = new List<GrainClassMetadata>();
    }
}
