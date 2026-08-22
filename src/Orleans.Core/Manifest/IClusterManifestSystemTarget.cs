using System.Collections.Generic;
using System.Collections.Immutable;
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
        /// Gets an updated cluster manifest if newer than the provided <paramref name="previousVersion"/>.
        /// </summary>
        /// <returns>The current cluster manifest, or <see langword="null"/> if it is not newer than the provided version.</returns>
        ValueTask<ClusterManifestUpdate?> GetClusterManifestUpdate(MajorMinorVersion previousVersion);

        /// <summary>
        /// Gets a hash summary for the current cluster manifest.
        /// </summary>
        /// <returns>The current cluster manifest hash summary.</returns>
        ValueTask<ClusterManifestHashSummary> GetClusterManifestHashSummary();

        /// <summary>
        /// Gets the hash of the local silo manifest.
        /// </summary>
        /// <returns>The hash of the local silo manifest.</returns>
        ValueTask<ManifestHash> GetSiloManifestHash();

        /// <summary>
        /// Gets the local silo manifest if the provided hash matches it.
        /// </summary>
        /// <param name="hash">The expected manifest hash.</param>
        /// <returns>The local silo manifest, or <see langword="null"/> if the hash does not match.</returns>
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
