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
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterManifest"/> class.
        /// </summary>
        /// <param name="version">
        /// The manifest version.
        /// </param>
        /// <param name="silos">
        /// The silo manifests.
        /// </param>
        /// <param name="allGrainManifests">
        /// All grain manifests.
        /// </param>
        public ClusterManifest(
            MajorMinorVersion version,
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ImmutableArray<GrainManifest> allGrainManifests)
        {
            this.Version = version;
            this.Silos = silos;
            this.AllGrainManifests = allGrainManifests;
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
        [Id(2)]
        public ImmutableArray<GrainManifest> AllGrainManifests { get; }
    }
}
