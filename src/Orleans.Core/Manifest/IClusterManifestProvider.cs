using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// Provides access to the cluster manifest.
    /// </summary>
    /// <seealso cref="ClusterManifest"/>
    public interface IClusterManifestProvider
    {
        /// <summary>
        /// Gets the current cluster manifest.
        /// </summary>
        ClusterManifest Current { get; }

        /// <summary>
        /// Gets the stream of cluster manifest updates.
        /// </summary>
        IAsyncEnumerable<ClusterManifest> Updates { get; }

        /// <summary>
        /// Gets the local grain manifest.
        /// </summary>
        GrainManifest LocalGrainManifest { get; }
    }
}
