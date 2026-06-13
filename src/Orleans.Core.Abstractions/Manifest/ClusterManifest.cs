using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
            EnsureDefaultKeyComparer(silos, nameof(silos));
            silos = silos.WithComparers(
                EqualityComparer<SiloAddress>.Default,
                EqualityComparer<GrainManifest>.Default);
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
            Debug.Assert(HasDefaultComparers(silos));
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
                var grains = DeduplicateProperties(manifest.Grains, canonicalGrainProperties);
                var interfaces = DeduplicateProperties(manifest.Interfaces, canonicalInterfaceProperties);
                return ReferenceEquals(grains, manifest.Grains) && ReferenceEquals(interfaces, manifest.Interfaces) ? manifest : new GrainManifest(grains, interfaces);
            }

            static ImmutableDictionary<TKey, TValue> DeduplicateProperties<TKey, TValue>(
                ImmutableDictionary<TKey, TValue> properties,
                Dictionary<TValue, TValue> canonicalProperties)
                where TKey : notnull
                where TValue : class
            {
                Debug.Assert(HasDefaultComparers(properties));
                ImmutableDictionary<TKey, TValue>.Builder? builder = null;
                foreach (var entry in properties)
                {
                    if (canonicalProperties.TryGetValue(entry.Value, out var canonicalProperty))
                    {
                        // The lookup above uses structural equality. Reference equality only tells us whether
                        // canonical replacement is needed.
                        if (!ReferenceEquals(canonicalProperty, entry.Value))
                        {
                            builder ??= properties.ToBuilder();
                            builder.Remove(entry.Key);
                            builder.Add(entry.Key, canonicalProperty);
                        }
                    }
                    else
                    {
                        canonicalProperties.Add(entry.Value, entry.Value);
                    }
                }

                return builder?.ToImmutable() ?? properties;
            }
        }

        private static bool HasDefaultComparers<TKey, TValue>(ImmutableDictionary<TKey, TValue> dictionary)
            where TKey : notnull
            => ReferenceEquals(dictionary.KeyComparer, EqualityComparer<TKey>.Default)
                && ReferenceEquals(dictionary.ValueComparer, EqualityComparer<TValue>.Default);

        private static void EnsureDefaultKeyComparer<TKey, TValue>(ImmutableDictionary<TKey, TValue> dictionary, string paramName)
            where TKey : notnull
        {
            if (!ReferenceEquals(dictionary.KeyComparer, EqualityComparer<TKey>.Default))
            {
                throw new ArgumentException("The dictionary must use the default key comparer.", paramName);
            }
        }
    }
}
