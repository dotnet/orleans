using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about types which are available in the cluster.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class ClusterManifest
    {
        [NonSerialized]
        private ImmutableArray<GrainManifest>? _allGrainManifests;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterManifest"/> class.
        /// </summary>
        /// <param name="version">
        /// The manifest version.
        /// </param>
        /// <param name="silos">
        /// The silo manifests.
        /// </param>
        public ClusterManifest(
            MajorMinorVersion version,
            ImmutableDictionary<SiloAddress, GrainManifest> silos)
        {
            this.Version = version;
            this.Silos = silos;
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
        public ImmutableDictionary<SiloAddress, GrainManifest> Silos { get; }

        /// <summary>
        /// Gets all grain manifests.
        /// </summary>
        public ImmutableArray<GrainManifest> AllGrainManifests => _allGrainManifests ??= Silos.Values.ToImmutableArray();
    }
}
