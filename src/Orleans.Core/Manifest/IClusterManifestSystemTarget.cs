using System;
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
        ValueTask<GetClusterManifestResult> GetClusterManifest(MajorMinorVersion version);
    }

    [Serializable, GenerateSerializer, Immutable]
    public class GetClusterManifestResult
    {
        [Id(0)]
        public bool IsVersionChange { get; set; }
        [Id(1)]
        public ClusterManifest ClusterManifest { get; set; }
    }
}
