using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Information about available grains.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class GrainManifest
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainManifest"/> class.
        /// </summary>
        /// <param name="grains">
        /// The grain properties.
        /// </param>
        /// <param name="interfaces">
        /// The interface properties.
        /// </param>
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
        [Id(0)]
        public ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties> Interfaces { get; }

        /// <summary>
        /// Gets the grain types available on this silo.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<GrainType, GrainProperties> Grains { get; }
    }
}
