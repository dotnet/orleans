using System.Collections.Immutable;
using System.Threading;
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
        [Alias("40D39F85")]
        ValueTask<ClusterManifest> GetClusterManifest(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets an updated cluster manifest if newer than the provided <paramref name="previousVersion"/>.
        /// </summary>
        /// <returns>The current cluster manifest, or <see langword="null"/> if it is not newer than the provided version.</returns>
        [Alias("4EFCA109")]
        ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion previousVersion, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents an update to the cluster manifest.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class ClusterManifestUpdate
    {
        public ClusterManifestUpdate(
            MajorMinorVersion manifestVersion,
            ImmutableDictionary<SiloAddress, GrainManifest> siloManifests,
            bool includesAllActiveServers)
        {
            Version = manifestVersion;
            SiloManifests = siloManifests;
            IncludesAllActiveServers = includesAllActiveServers;
        }

        /// <summary>
        /// Gets the version of this instance.
        /// </summary>
        [Id(0)]
        public MajorMinorVersion Version { get; }

        /// <summary>
        /// Gets the manifests for each silo in the cluster.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<SiloAddress, GrainManifest> SiloManifests { get; }

        /// <summary>
        /// Gets a value indicating whether this update includes all active servers.
        /// </summary>
        [Id(2)]
        public bool IncludesAllActiveServers { get; } 
    }
}
