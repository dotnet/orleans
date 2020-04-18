using System.Collections.Generic;
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
                ThrowNotFoundException(grainType);
            }

            return result;
        }

        /// <summary>
        /// Gets the grain properties for the provided type.
        /// </summary>
        public bool TryGetGrainProperties(GrainType grainType, out GrainProperties properties)
        {
            var clusterManifest = _clusterManifestProvider.Current;

            foreach (var entry in clusterManifest.Silos)
            {
                var manifest = entry.Value;

                if (manifest.Grains.TryGetValue(grainType, out properties))
                {
                    return true;
                }
            }

            properties = default;
            return false;
        }

        private static void ThrowNotFoundException(GrainType grainType)
        {
            throw new KeyNotFoundException($"Could not find grain properties for grain type {grainType}");
        }
    }
}
