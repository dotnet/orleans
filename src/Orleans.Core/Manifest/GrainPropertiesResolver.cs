using System.Collections.Immutable;
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
        /// Creates a <see cref="GrainPropertiesResolver"/> instance.
        /// </summary>
        public GrainPropertiesResolver(IClusterManifestProvider clusterManifestProvider)
        {
            _clusterManifestProvider = clusterManifestProvider;
        }

        /// <summary>
        /// Gets the grain properties for the provided type.
        /// </summary>
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
        public bool TryGetGrainProperties(GrainType grainType, out GrainProperties properties)
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
