using System.Collections.Generic;
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

        /// <summary>
        /// Gets a hash summary for the current cluster manifest.
        /// </summary>
        /// <returns>The current cluster manifest hash summary.</returns>
        [Alias("25AE6E4A")]
        ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary();

        /// <summary>
        /// Gets the hash of the local silo manifest.
        /// </summary>
        /// <returns>The hash of the local silo manifest.</returns>
        [Alias("3D9B7FE6")]
        ValueTask<ManifestHash> GetSiloManifestHash();

        /// <summary>
        /// Gets the local silo manifest if the provided hash matches it.
        /// </summary>
        /// <param name="hash">The expected manifest hash.</param>
        /// <returns>The local silo manifest, or <see langword="null"/> if the hash does not match.</returns>
        [Alias("93B8854F")]
        ValueTask<GrainManifest?> GetSiloManifestByHash(ManifestHash hash);
    }

    /// <summary>
    /// Identifies a manifest by its canonical content hash.
    /// </summary>
    [GenerateSerializer, Immutable]
    internal readonly struct ManifestHash : System.IEquatable<ManifestHash>
    {
        public ManifestHash(string value)
        {
            Value = value;
        }

        [Id(0)]
        public string Value { get; }

        public bool Equals(ManifestHash other) => string.Equals(Value, other.Value, System.StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ManifestHash other && Equals(other);

        public override int GetHashCode() => System.StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(ManifestHash left, ManifestHash right) => left.Equals(right);

        public static bool operator !=(ManifestHash left, ManifestHash right) => !left.Equals(right);
    }

    /// <summary>
    /// Represents a hash summary for a cluster manifest.
    /// </summary>
    [GenerateSerializer, Immutable]
    internal sealed class ClusterManifestHashSummary
    {
        public ClusterManifestHashSummary(
            MajorMinorVersion version,
            Dictionary<SiloAddress, ManifestHash> siloManifestHashes)
        {
            Version = version;
            SiloManifestHashes = siloManifestHashes.ToImmutableDictionary();
        }

        /// <summary>
        /// Gets the cluster manifest version.
        /// </summary>
        [Id(0)]
        public MajorMinorVersion Version { get; }

        /// <summary>
        /// Gets the manifest hash for each silo.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<SiloAddress, ManifestHash> SiloManifestHashes { get; }
    }

    /// <summary>
    /// Represents an update to the cluster manifest.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class ClusterManifestUpdate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterManifestUpdate"/> class.
        /// </summary>
        /// <param name="manifestVersion">The cluster manifest version.</param>
        /// <param name="siloManifests">The manifests for each silo in the cluster.</param>
        /// <param name="includesAllActiveServers">
        /// A value indicating whether <paramref name="siloManifests"/> includes every active server.
        /// </param>
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
