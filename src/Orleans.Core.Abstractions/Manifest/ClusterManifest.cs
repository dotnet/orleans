using System;
using System.Collections.Generic;
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
        public ClusterManifest(
            MajorMinorVersion version,
            ImmutableDictionary<SiloAddress, GrainManifest> silos)
            : this(version, silos, silos.Values.ToImmutableArray())
        {
        }

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
        /// All grain manifests, including manifests which are not associated with entries in <paramref name="silos"/>.
        /// </param>
        public ClusterManifest(
            MajorMinorVersion version,
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ImmutableArray<GrainManifest> allGrainManifests)
        {
            ArgumentNullException.ThrowIfNull(silos);
            var deduplicated = Deduplicate(silos, allGrainManifests);
            Version = version;
            Silos = deduplicated.Silos;
            AllGrainManifests = deduplicated.AllGrainManifests;
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
        /// Gets all unique grain manifests.
        /// </summary>
        [Id(2)]
        public ImmutableArray<GrainManifest> AllGrainManifests { get; }

        private static (ImmutableDictionary<SiloAddress, GrainManifest> Silos, ImmutableArray<GrainManifest> AllGrainManifests) Deduplicate(
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ImmutableArray<GrainManifest> allGrainManifests)
        {
            var canonicalGrainProperties = new Dictionary<GrainProperties, GrainProperties>();
            var canonicalInterfaceProperties = new Dictionary<GrainInterfaceProperties, GrainInterfaceProperties>();
            var canonicalManifests = new Dictionary<GrainManifest, GrainManifest>();
            var uniqueManifests = ImmutableArray.CreateBuilder<GrainManifest>();
            var siloBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, GrainManifest>(silos.KeyComparer, silos.ValueComparer);

            foreach (var entry in silos)
            {
                siloBuilder[entry.Key] = GetCanonicalManifest(entry.Value);
            }

            if (!allGrainManifests.IsDefault)
            {
                foreach (var manifest in allGrainManifests)
                {
                    GetCanonicalManifest(manifest);
                }
            }

            return (siloBuilder.ToImmutable(), uniqueManifests.ToImmutable());

            GrainManifest GetCanonicalManifest(GrainManifest manifest)
            {
                manifest = DeduplicateManifest(manifest);
                if (canonicalManifests.TryGetValue(manifest, out var canonicalManifest))
                {
                    return canonicalManifest;
                }

                canonicalManifests.Add(manifest, manifest);
                uniqueManifests.Add(manifest);
                return manifest;
            }

            GrainManifest DeduplicateManifest(GrainManifest manifest)
            {
                var grains = DeduplicateProperties(manifest.Grains, canonicalGrainProperties, out var grainsModified);
                var interfaces = DeduplicateProperties(manifest.Interfaces, canonicalInterfaceProperties, out var interfacesModified);
                return grainsModified || interfacesModified ? new GrainManifest(grains, interfaces) : manifest;
            }

            static ImmutableDictionary<TKey, TValue> DeduplicateProperties<TKey, TValue>(
                ImmutableDictionary<TKey, TValue> properties,
                Dictionary<TValue, TValue> canonicalProperties,
                out bool modified)
                where TKey : notnull
                where TValue : class
            {
                var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>(properties.KeyComparer, properties.ValueComparer);
                modified = false;
                foreach (var entry in properties)
                {
                    if (canonicalProperties.TryGetValue(entry.Value, out var canonicalProperty))
                    {
                        if (!ReferenceEquals(canonicalProperty, entry.Value))
                        {
                            modified = true;
                        }

                        builder[entry.Key] = canonicalProperty;
                    }
                    else
                    {
                        canonicalProperties.Add(entry.Value, entry.Value);
                        builder[entry.Key] = entry.Value;
                    }
                }

                return modified ? builder.ToImmutable() : properties;
            }
        }
    }
}
