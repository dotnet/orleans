using System;
using System.Collections;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about types which are available in the cluster.
    /// </summary>
    [Serializable]
    public class ClusterManifest
    {
        /// <summary>
        /// Creates a new <see cref="ClusterManifest"/> instance.
        /// </summary>
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
        /// The version of this instance.
        /// </summary>
        public MajorMinorVersion Version { get; }

        /// <summary>
        /// Manifests for each silo in the cluster.
        /// </summary>
        public ImmutableDictionary<SiloAddress, GrainManifest> Silos { get; }

        /// <summary>
        /// All grain manifests.
        /// </summary>
        public ImmutableArray<GrainManifest> AllGrainManifests { get; }
    }
}
