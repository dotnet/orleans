using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Metadata
{
    /// <summary>
    /// Describes the bindings for a given grain type.
    /// </summary>
    /// <remarks>
    /// Bindings are a way to declaratively connect grains with other resources.
    /// </remarks>
    public class GrainBindings
    {
        public GrainBindings(GrainType grainType, ImmutableArray<ImmutableDictionary<string, string>> bindings)
        {
            this.GrainType = grainType;
            this.Bindings = bindings;
        }

        /// <summary>
        /// The grain type.
        /// </summary>
        public GrainType GrainType { get; }

        /// <summary>
        /// The bindings for the specified grain type.
        /// </summary>
        public ImmutableArray<ImmutableDictionary<string, string>> Bindings { get; }
    }

    /// <summary>
    /// Resolves bindings for grain types.
    /// </summary>
    public class GrainBindingsResolver
    {
        private const string BindingPrefix = WellKnownGrainTypeProperties.BindingPrefix + ".";
        private const char BindingIndexEnd = '.';
        private readonly object _lockObj = new object();
        private readonly ConcurrentDictionary<GenericGrainType, GrainType> _genericMapping = new ConcurrentDictionary<GenericGrainType, GrainType>();
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private Cache _cache;

        /// <summary>
        /// Creates a new <see cref="GrainBindingsResolver"/> instance.
        /// </summary>
        public GrainBindingsResolver(IClusterManifestProvider clusterManifestProvider)
        {
            _clusterManifestProvider = clusterManifestProvider;
            _cache = BuildCache(_clusterManifestProvider.Current);
        }

        /// <summary>
        /// Gets bindings for the provided grain type.
        /// </summary>
        public GrainBindings GetBindings(GrainType grainType)
        {
            GrainType lookupType;
            if (GenericGrainType.TryParse(grainType, out var generic))
            {
                if (!_genericMapping.TryGetValue(generic, out lookupType))
                {
                    lookupType = _genericMapping[generic] = generic.GetUnconstructedGrainType().GrainType;
                }
            }
            else
            {
                lookupType = grainType;
            }

            var cache = GetCache();
            if (cache.Map.TryGetValue(lookupType, out var result))
            {
                return result;
            }

            return new GrainBindings(grainType, ImmutableArray<ImmutableDictionary<string, string>>.Empty);
        }

        /// <summary>
        /// Gets all bindings.
        /// </summary>
        public (MajorMinorVersion Version, ImmutableDictionary<GrainType, GrainBindings> Bindings) GetAllBindings()
        {
            var cache = GetCache();
            return (cache.Version, cache.Map);
        }

        private Cache GetCache()
        {
            var cache = _cache;
            var manifest = _clusterManifestProvider.Current;
            if (manifest.Version == cache.Version)
            {
                return cache;
            }

            lock (_lockObj)
            {
                cache = _cache;
                manifest = _clusterManifestProvider.Current;
                if (manifest.Version == cache.Version)
                {
                    return cache;
                }

                return _cache = BuildCache(manifest);
            }
        }

        private Cache BuildCache(ClusterManifest clusterManifest)
        {
            var result = new Dictionary<GrainType, GrainBindings>();

            var bindings = new Dictionary<string, Dictionary<string, string>>();
            foreach (var manifest in clusterManifest.AllGrainManifests)
            {
                foreach (var grainType in manifest.Grains)
                {
                    var id = grainType.Key;
                    if (result.ContainsKey(id)) continue;
                    bindings.Clear();
                    foreach (var pair in grainType.Value.Properties)
                    {
                        if (TryExtractBindingProperty(pair, out var binding))
                        {
                            if (!bindings.TryGetValue(binding.Index, out var properties))
                            {
                                bindings[binding.Index] = properties = new Dictionary<string, string>();
                            }

                            properties.Add(binding.Key, binding.Value);
                        }
                    }

                    var builder = ImmutableArray.CreateBuilder<ImmutableDictionary<string, string>>();
                    foreach (var binding in bindings.Values)
                    {
                        builder.Add(ImmutableDictionary.CreateRange(binding));
                    }

                    result.Add(id, new GrainBindings(id, builder.ToImmutable()));
                }
            }

            return new Cache(clusterManifest.Version, result.ToImmutableDictionary());

            bool TryExtractBindingProperty(KeyValuePair<string, string> property, out (string Index, string Key, string Value) result)
            {
                if (!property.Key.StartsWith(BindingPrefix, StringComparison.Ordinal)
                    || property.Key.IndexOf(BindingIndexEnd, BindingPrefix.Length) is int indexEndIndex && indexEndIndex < 0)
                {
                    result = default;
                    return false;
                }

                var bindingIndex = property.Key.Substring(BindingPrefix.Length, indexEndIndex - BindingPrefix.Length);
                var bindingKey = property.Key.Substring(indexEndIndex + 1);

                if (string.IsNullOrWhiteSpace(bindingIndex) || string.IsNullOrWhiteSpace(bindingKey))
                {
                    result = default;
                    return false;
                }

                result = (bindingIndex, bindingKey, property.Value);
                return true;
            }
        }

        private class Cache
        {
            public Cache(MajorMinorVersion version, ImmutableDictionary<GrainType, GrainBindings> map)
            {
                this.Version = version;
                this.Map = map;
            }

            public MajorMinorVersion Version { get; }
            public ImmutableDictionary<GrainType, GrainBindings> Map { get; }
        }
    }
}
