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

        /// <summary>
        /// Gets the current cluster manifest if it is newer than the provided <paramref name="version"/>.
        /// </summary>
        /// <returns>The current cluster manifest, or <see langword="null"/> if it is not newer than the provided version.</returns>
        ValueTask<ClusterManifestUpdate> GetClusterManifestIfNewer(MajorMinorVersion version);
    }

    [GenerateSerializer, Immutable]
    public readonly struct ClusterManifestUpdate
    {
        public ClusterManifestUpdate(ClusterManifest manifest, bool includesAllActiveServers)
        {
            Manifest = manifest;
            IncludesAllActiveServers = includesAllActiveServers;
        }

        [Id(0)]
        public ClusterManifest Manifest { get; }

        [Id(1)]
        public bool IncludesAllActiveServers { get; } 
    }
}
