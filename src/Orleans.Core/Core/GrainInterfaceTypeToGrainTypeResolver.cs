using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans
{
    /// <summary>
    /// Associates <see cref="GrainInterfaceType"/>s with a compatible <see cref="GrainType"/>.
    /// </summary>
    /// <remarks>
    /// This is primarily intended for end-users calling <see cref="IGrainFactory"/> methods without needing to be overly explicit.
    /// </remarks>
    public class GrainInterfaceTypeToGrainTypeResolver
    {
        private readonly object _lockObj = new object();
        private readonly ConcurrentDictionary<GrainInterfaceType, GrainType> _genericMapping = new ConcurrentDictionary<GrainInterfaceType, GrainType>();
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private Cache _cache;

        /// <summary>
        /// Creates a new instance of the <see cref="GrainInterfaceTypeToGrainTypeResolver"/> class.
        /// </summary>
        /// <param name="clusterManifestProvider">The cluster manifest provider.</param>
        public GrainInterfaceTypeToGrainTypeResolver(IClusterManifestProvider clusterManifestProvider)
        {
            _clusterManifestProvider = clusterManifestProvider;
        }

        /// <summary>
        /// Returns the <see cref="GrainType"/> which supports the provided <see cref="GrainInterfaceType"/> and which has an implementing type name beginning with the provided prefix string.
        /// </summary>
        public GrainType GetGrainType(GrainInterfaceType interfaceType, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return GetGrainType(interfaceType);
            }

            GrainType result = default;

            GrainInterfaceType lookupType;
            if (GenericGrainInterfaceType.TryParse(interfaceType, out var genericInterface))
            {
                lookupType = genericInterface.GetGenericGrainType().Value;
            }
            else
            {
                lookupType = interfaceType;
            }

            var cache = GetCache();
            if (cache.Map.TryGetValue(lookupType, out var entry))
            {
                var hasCandidate = false;
                foreach (var impl in entry.Implementations)
                {
                    if (impl.Prefix.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        if (impl.Prefix.Length == prefix.Length)
                        {
                            // Exact matches take precedence
                            result = impl.GrainType;
                            break;
                        }

                        if (hasCandidate)
                        {
                            var candidates = string.Join(", ", entry.Implementations.Select(i => $"{i.GrainType} ({i.Prefix})"));
                            throw new ArgumentException($"Unable to identify a single appropriate grain type for interface {interfaceType} with implementation prefix \"{prefix}\". Candidates: {candidates}");
                        }

                        result = impl.GrainType;
                        hasCandidate = true;
                    }
                }
            }

            if (result.IsDefault)
            {
                throw new ArgumentException($"Could not find an implementation matching prefix \"{prefix}\" for interface {interfaceType}");
            }

            if (GenericGrainType.TryParse(result, out var genericGrainType) && !genericGrainType.IsConstructed)
            {
                result = genericGrainType.GrainType.GetConstructed(genericInterface.Value);
            }

            return result;
        }

        /// <summary>
        /// Returns a <see cref="GrainType"/> which implements the provided <see cref="GrainInterfaceType"/>.
        /// </summary>
        public GrainType GetGrainType(GrainInterfaceType interfaceType)
        {
            GrainType result = default;
            var cache = GetCache();
            if (cache.Map.TryGetValue(interfaceType, out var entry))
            {
                if (!entry.PrimaryImplementation.IsDefault)
                {
                    result = entry.PrimaryImplementation;
                }
                else if (entry.Implementations.Count == 1)
                {
                    result = entry.Implementations[0].GrainType;
                }
                else if (entry.Implementations.Count > 1)
                {
                    var candidates = string.Join(", ", entry.Implementations.Select(i => $"{i.GrainType} ({i.Prefix})"));
                    throw new ArgumentException($"Unable to identify a single appropriate grain type for interface {interfaceType}. Candidates: {candidates}");
                }
                else
                {
                    // No implementations
                }
            }
            else if (_genericMapping.TryGetValue(interfaceType, out result))
            {
            }
            else if (GenericGrainInterfaceType.TryParse(interfaceType, out var genericInterface))
            {
                var unconstructedInterface = genericInterface.GetGenericGrainType();
                var unconstructed = GetGrainType(unconstructedInterface.Value);
                if (GenericGrainType.TryParse(unconstructed, out var genericGrainType))
                {
                    if (genericGrainType.IsConstructed)
                    {
                        result = genericGrainType.GrainType;
                    }
                    else
                    {
                        result = genericGrainType.GrainType.GetConstructed(genericInterface.Value);
                    }
                }
                else
                {
                    result = unconstructed;
                }
                _genericMapping[interfaceType] = result;
            }

            if (result.IsDefault)
            {
                throw new ArgumentException($"Could not find an implementation for interface {interfaceType}");
            }

            return result;
        }

        /// <summary>
        /// Returns the cache, rebuilding it if it is out of date.
        /// </summary>
        /// <returns>The cache.</returns>
        private Cache GetCache()
        {
            if (_cache is Cache cache && cache.Version == _clusterManifestProvider.Current.Version)
            {
                return cache;
            }

            lock (_lockObj)
            {
                var manifest = _clusterManifestProvider.Current;
                cache = _cache;
                if (cache is not null && cache.Version == manifest.Version)
                {
                    return cache;
                }

                return _cache = BuildCache(manifest);
            }
        }

        /// <summary>
        /// Builds a cached resolution mapping.
        /// </summary>
        /// <param name="clusterManifest">The current cluster manifest.</param>
        /// <returns>The cache.</returns>
        private static Cache BuildCache(ClusterManifest clusterManifest)
        {
            var result = new Dictionary<GrainInterfaceType, CacheEntry>();

            foreach (var manifest in clusterManifest.AllGrainManifests)
            {
                foreach (var grainType in manifest.Grains)
                {
                    var id = grainType.Key;
                    grainType.Value.Properties.TryGetValue(WellKnownGrainTypeProperties.TypeName, out var typeName);
                    grainType.Value.Properties.TryGetValue(WellKnownGrainTypeProperties.FullTypeName, out var fullTypeName);
                    foreach (var property in grainType.Value.Properties)
                    {
                        if (!property.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)) continue;
                        var implemented = GrainInterfaceType.Create(property.Value);
                        string interfaceTypeName;
                        if (manifest.Interfaces.TryGetValue(implemented, out var interfaceProperties))
                        {
                            interfaceProperties.Properties.TryGetValue(WellKnownGrainInterfaceProperties.TypeName, out interfaceTypeName);
                        }
                        else
                        {
                            interfaceTypeName = null;
                        }

                        // Try to work out the best primary implementation
                        result.TryGetValue(implemented, out var entry);

                        var implementations = entry.Implementations ?? new List<(string Prefix, GrainType GrainType)>();
                        if (!implementations.Contains((fullTypeName, id))) implementations.Add((fullTypeName, id));

                        GrainType primaryImplementation;
                        if (!entry.PrimaryImplementation.IsDefault)
                        {
                            primaryImplementation = entry.PrimaryImplementation;
                        }
                        else if (interfaceProperties?.Properties is { } props && props.TryGetValue(WellKnownGrainInterfaceProperties.DefaultGrainType, out var defaultTypeString))
                        {
                            // A specified default grain type trumps others.
                            primaryImplementation = GrainType.Create(defaultTypeString);
                        }
                        else if (string.Equals(interfaceTypeName?.Substring(1), typeName, StringComparison.Ordinal))
                        {
                            // Otherwise, a substring match on the interface name, dropping the 'I', is used.
                            primaryImplementation = id;
                        }
                        else
                        {
                            primaryImplementation = default;
                        }
                        result[implemented] = new CacheEntry(primaryImplementation, implementations);
                    }
                }
            }

            return new Cache(clusterManifest.Version, result);
        }

        /// <summary>
        /// Contains a mapping from grain interface type to the implementations of that interface.
        /// </summary>
        private class Cache
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="Cache"/> class.
            /// </summary>
            /// <param name="version">The cluster manifest version which this instance corresponds to.</param>
            /// <param name="map">The interface map.</param>
            public Cache(MajorMinorVersion version, Dictionary<GrainInterfaceType, CacheEntry> map)
            {
                this.Version = version;
                this.Map = map;
            }

            /// <summary>
            /// Gets the cluster manifest version which this cache corresponds to.
            /// </summary>
            public MajorMinorVersion Version { get; }

            /// <summary>
            /// Gets the mapping from grain interface type to implementations.
            /// </summary>
            public Dictionary<GrainInterfaceType, CacheEntry> Map { get; }
        }

        /// <summary>
        /// Represents the implementation <see cref="GrainType"/> values for a grain interface type.
        /// </summary>
        private readonly struct CacheEntry
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="CacheEntry"/> struct.
            /// </summary>
            /// <param name="primaryImplementation">The primary implementation type.</param>
            /// <param name="implementations">The set of other implementations along with their grain type prefixes.</param>
            public CacheEntry(GrainType primaryImplementation, List<(string Prefix, GrainType GrainType)> implementations)
            {
                this.PrimaryImplementation = primaryImplementation;
                this.Implementations = implementations;
            }

            /// <summary>
            /// Gets the primary implementation type.
            /// </summary>
            public GrainType PrimaryImplementation { get; }

            /// <summary>
            /// Gets the collection of implementation types with their class name prefixes.
            /// </summary>
            public List<(string Prefix, GrainType GrainType)> Implementations { get; }
        }
    }
}
