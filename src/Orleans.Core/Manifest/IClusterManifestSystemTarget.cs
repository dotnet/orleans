using System.Threading.Tasks;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal interface for exposing the cluster manifest.
    /// </summary>
    internal interface IClusterManifestSystemTarget : ISystemTarget
    {
        /// <summary>
        /// Gets the current cluster manifest.
        /// </summary>
        /// <returns>The current cluster manifest.</returns>
        ValueTask<ClusterManifest> GetClusterManifest();
    }
}
