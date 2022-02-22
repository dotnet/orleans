using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Responsible for resolving <see cref="GrainProperties"/> for <see cref="GrainType"/> values.
    /// </summary>
    public class GrainPropertiesResolver
    {
        private readonly IClusterManifestProvider _clusterManifestProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainPropertiesResolver"/> class.
        /// </summary>
        /// <param name="clusterManifestProvider">
        /// The cluster manifest provider.
        /// </param>
        public GrainPropertiesResolver(IClusterManifestProvider clusterManifestProvider)
        {
            _clusterManifestProvider = clusterManifestProvider;
        }

        /// <summary>
        /// Gets the grain properties for the provided type.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <returns>
        /// The grain properties.
        /// </returns>
        public GrainProperties GetGrainProperties(GrainType grainType)
        {
            if (!TryGetGrainProperties(grainType, out var result))
            {
                //ThrowNotFoundException(grainType);
                result = new GrainProperties(ImmutableDictionary<string, string>.Empty);
            }

            return result;
        }

        /// <summary>
        /// Gets the grain properties for the provided type.
        /// </summary>
        /// <param name="grainType">
        /// The grain type.
        /// </param>
        /// <param name="properties">
        /// The grain properties.
        /// </param>
        /// <returns>
        /// A value indicating whether grain properties could be found for the provided grain type.
        /// </returns>
        public bool TryGetGrainProperties(GrainType grainType, [NotNullWhen(true)] out GrainProperties properties)
        {
            var clusterManifest = _clusterManifestProvider.Current;
            if (clusterManifest is null)
            {
                properties = default;
                return false;
            }

            GrainType lookupKey;
            if (GenericGrainType.TryParse(grainType, out var generic))
            {
                lookupKey = generic.GetUnconstructedGrainType().GrainType;
            }
            else
            {
                lookupKey = grainType;
            }

            foreach (var manifest in clusterManifest.AllGrainManifests)
            {
                if (manifest.Grains.TryGetValue(lookupKey, out properties))
                {
                    return true;
                }
            }

            properties = default;
            return false;
        }
    }
}
