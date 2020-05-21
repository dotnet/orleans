using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Associates <see cref="GrainInterfaceId"/>s with a compatible <see cref="GrainType"/>.
    /// </summary>
    /// <remarks>
    /// This is primarily intended for end-users calling <see cref="IGrainFactory"/> methods without needing to be overly explicit.
    /// </remarks>
    public class GrainInterfaceToTypeResolver
    {
        private readonly object _lockObj = new object();
        private readonly ConcurrentDictionary<GrainInterfaceId, GrainType> _genericMapping = new ConcurrentDictionary<GrainInterfaceId, GrainType>();
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private Cache _cache;

        public GrainInterfaceToTypeResolver(IClusterManifestProvider clusterManifestProvider)
        {
            _clusterManifestProvider = clusterManifestProvider;
        }

        /// <summary>
        /// Returns the <see cref="GrainType"/> which supports the provided <see cref="GrainInterfaceId"/> and which has an implementing type name beginning with the provided prefix string.
        /// </summary>
        public GrainType GetGrainType(GrainInterfaceId interfaceId, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return GetGrainType(interfaceId);
            }

            GrainType result = default;

            GrainInterfaceId lookupId;
            if (GenericGrainInterfaceId.TryParse(interfaceId, out var genericInterface))
            {
                lookupId = genericInterface.GetGenericGrainType().Value;
            }
            else
            {
                lookupId = interfaceId;
            }

            var cache = GetCache();
            if (cache.Map.TryGetValue(lookupId, out var entry))
            {
                var hasCandidate = false;
                foreach (var impl in entry.Implementations)
                {
                    if (impl.Prefix.StartsWith(prefix))
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
                            throw new ArgumentException($"Unable to identify a single appropriate grain type for interface {interfaceId} with implementation prefix \"{prefix}\". Candidates: {candidates}");
                        }

                        result = impl.GrainType;
                        hasCandidate = true;
                    }
                }
            }

            if (result.IsDefault)
            {
                throw new ArgumentException($"Could not find an implementation matching prefix \"{prefix}\" for interface {interfaceId}");
            }

            if (GenericGrainType.TryParse(result, out var genericGrainType))
            {
                if (genericGrainType.IsConstructed)
                {
                    _genericMapping[interfaceId] = genericGrainType.GrainType;
                    result = genericGrainType.GrainType;
                }
                else
                {
                    var args = genericInterface.GetArgumentsString();
                    var constructed = GrainType.Create(genericGrainType.GrainType.ToStringUtf8() + args);
                    _genericMapping[interfaceId] = constructed;
                    result = constructed;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a <see cref="GrainType"/> which implements the provided <see cref="GrainInterfaceId"/>.
        /// </summary>
        public GrainType GetGrainType(GrainInterfaceId interfaceId)
        {
            GrainType result;
            var cache = GetCache();
            if (cache.Map.TryGetValue(interfaceId, out var entry))
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
                    throw new ArgumentException($"Unable to identify a single appropriate grain type for interface {interfaceId}. Candidates: {candidates}");
                }
                else
                {
                    // No implementations
                    result = default;
                }
            }
            else if (_genericMapping.TryGetValue(interfaceId, out var generic))
            {
                result = generic;
            }
            else if (GenericGrainInterfaceId.TryParse(interfaceId, out var genericInterface))
            {
                var unconstructedInterface = genericInterface.GetGenericGrainType();
                var unconstructed = GetGrainType(unconstructedInterface.Value);
                if (GenericGrainType.TryParse(unconstructed, out var genericGrainType))
                {
                    if (genericGrainType.IsConstructed)
                    {
                        _genericMapping[interfaceId] = genericGrainType.GrainType;
                        result = genericGrainType.GrainType;
                    }
                    else
                    {
                        var args = genericInterface.GetArgumentsString();
                        var constructed = GrainType.Create(genericGrainType.GrainType.ToStringUtf8() + args);
                        _genericMapping[interfaceId] = constructed;
                        result = constructed;
                    }
                }
                else
                {
                    _genericMapping[interfaceId] = unconstructed;
                    result = unconstructed;
                }
            }
            else
            {
                result = default;
            }

            if (result.IsDefault)
            {
                throw new ArgumentException($"Could not find an implementation for interface {interfaceId}");
            }

            return result;
        }

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
                if (cache is object && cache.Version == manifest.Version)
                {
                    return cache;
                }

                return _cache = BuildCache(manifest);
            }
        }

        private static Cache BuildCache(ClusterManifest clusterManifest)
        {
            var result = new Dictionary<GrainInterfaceId, CacheEntry>();

            foreach (var manifest in clusterManifest.AllGrainManifests)
            {
                GrainType knownPrimary = default;
                foreach (var grainInterface in manifest.Interfaces)
                {
                    var id = grainInterface.Key;

                    if (grainInterface.Value.Properties.TryGetValue(WellKnownGrainInterfaceProperties.DefaultGrainType, out var defaultTypeString))
                    {
                        knownPrimary = GrainType.Create(defaultTypeString);
                        continue;
                    } 
                }

                foreach (var grainType in manifest.Grains)
                {
                    var id = grainType.Key;
                    grainType.Value.Properties.TryGetValue(WellKnownGrainTypeProperties.TypeName, out var typeName);
                    grainType.Value.Properties.TryGetValue(WellKnownGrainTypeProperties.FullTypeName, out var fullTypeName);
                    foreach (var implemented in SupportedGrainInterfaces(grainType.Value))
                    {
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
                        GrainType primaryImplementation;
                        if (!knownPrimary.IsDefault)
                        {
                            primaryImplementation = knownPrimary;
                        }
                        else if (!entry.PrimaryImplementation.IsDefault)
                        {
                            primaryImplementation = entry.PrimaryImplementation;
                        }
                        else if (string.Equals(interfaceTypeName?.Substring(1), typeName, StringComparison.Ordinal))
                        {
                            primaryImplementation = id;
                        }
                        else
                        {
                            primaryImplementation = default;
                        }

                        var implementations = entry.Implementations ?? new List<(string Prefix, GrainType GrainType)>();
                        if (!implementations.Contains((fullTypeName, id))) implementations.Add((fullTypeName, id));
                        result[implemented] = new CacheEntry(primaryImplementation, implementations);
                    }
                }
            }

            return new Cache(clusterManifest.Version, result);

            IEnumerable<GrainInterfaceId> SupportedGrainInterfaces(GrainProperties grain)
            {
                foreach (var property in grain.Properties)
                {
                    if (property.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix))
                    {
                        yield return GrainInterfaceId.Create(property.Value);
                    }
                }
            }
        }

        private class Cache
        {
            public Cache(MajorMinorVersion version, Dictionary<GrainInterfaceId, CacheEntry> map)
            {
                this.Version = version;
                this.Map = map;
            }

            public MajorMinorVersion Version { get; }
            public Dictionary<GrainInterfaceId, CacheEntry> Map { get; }
        }

        private readonly struct CacheEntry
        {
            public CacheEntry(GrainType primaryImplementation, List<(string Prefix, GrainType GrainType)> implementations)
            {
                this.PrimaryImplementation = primaryImplementation;
                this.Implementations = implementations;
            }

            public GrainType PrimaryImplementation { get; }
            public List<(string Prefix, GrainType GrainType)> Implementations { get; } 
        }
    }
}
