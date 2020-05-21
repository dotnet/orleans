using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about a silo.
    /// </summary>
    [Serializable]
    public class SiloManifest
    {
        /// <summary>
        /// Creates a new <see cref="SiloManifest"/> instance.
        /// </summary>
        public SiloManifest(
            ImmutableDictionary<GrainType, GrainProperties> grains,
            ImmutableDictionary<GrainInterfaceId, GrainInterfaceProperties> interfaces)
        {
            this.Interfaces = interfaces;
            this.Grains = grains;
        }

        /// <summary>
        /// Gets the interfaces available on this silo.
        /// </summary>
        public ImmutableDictionary<GrainInterfaceId, GrainInterfaceProperties> Interfaces { get; }

        /// <summary>
        /// Gets the grain types available on this silo.
        /// </summary>
        public ImmutableDictionary<GrainType, GrainProperties> Grains { get; }
    }
}
