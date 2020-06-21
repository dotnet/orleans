using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about available grains.
    /// </summary>
    [Serializable]
    public class GrainManifest
    {
        /// <summary>
        /// Creates a new <see cref="GrainManifest"/> instance.
        /// </summary>
        public GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties> grains,
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> interfaces)
        {
            this.Interfaces = interfaces;
            this.Grains = grains;
        }

        /// <summary>
        /// Gets the interfaces available on this silo.
        /// </summary>
        public ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> Interfaces { get; }

        /// <summary>
        /// Gets the grain types available on this silo.
        /// </summary>
        public ImmutableDictionary<GrainType, GrainProperties> Grains { get; }
    }
}
