using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Metadata;

namespace Orleans.Runtime.Versions
{
    /// <summary>
    /// Functionality for querying the declared version of grain interfaces.
    /// </summary>
    internal class GrainVersionManifest
    {
        private readonly object _lockObj = new object();
        private readonly ConcurrentDictionary<GrainInterfaceType, GrainInterfaceType> _genericInterfaceMapping = new ConcurrentDictionary<GrainInterfaceType, GrainInterfaceType>();
        private readonly ConcurrentDictionary<GrainType, GrainType> _genericGrainTypeMapping = new ConcurrentDictionary<GrainType, GrainType>();
        private readonly IClusterManifestProvider _clusterManifestProvider;
        private readonly Dictionary<GrainInterfaceType, ushort> _localVersions;
        private Cache _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainVersionManifest"/> class.
        /// </summary>
        /// <param name="clusterManifestProvider">The cluster manifest provider.</param>
        public GrainVersionManifest(IClusterManifestProvider clusterManifestProvider)
        {
            _clusterManifestProvider = clusterManifestProvider;
            _cache = BuildCache(clusterManifestProvider.Current);
            _localVersions = BuildLocalVersionMap(clusterManifestProvider.LocalGrainManifest);
        }

        /// <summary>
        /// Gets the current cluster manifest version.
        /// </summary>
        public MajorMinorVersion LatestVersion => _clusterManifestProvider.Current.Version;

        /// <summary>
        /// Gets the local version for a specified grain interface type.
        /// </summary>
        /// <param name="interfaceType">The grain interface type name.</param>
        /// <returns>The version of the specified grain interface.</returns>
        public ushort GetLocalVersion(GrainInterfaceType interfaceType)
        {
            if (_localVersions.TryGetValue(interfaceType, out var result))
            {
                return result;
            }

            if (_genericInterfaceMapping.TryGetValue(interfaceType, out var genericInterfaceId))
            {
                return GetLocalVersion(genericInterfaceId);
            }

            if (GenericGrainInterfaceType.TryParse(interfaceType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericInterfaceMapping[interfaceType] = generic.GetGenericGrainType().Value;
                return GetLocalVersion(genericId);
            }

            return 0;
        }

        /// <summary>
        /// Gets a collection of all known versions for a grain interface.
        /// </summary>
        /// <param name="interfaceType">The grain interface type name.</param>
        /// <returns>All known versions for the specified grain interface.</returns>
        public (MajorMinorVersion Version, ushort[] Result) GetAvailableVersions(GrainInterfaceType interfaceType)
        {
            var cache = GetCache();
            if (cache.AvailableVersions.TryGetValue(interfaceType, out var result))
            {
                return (cache.Version, result);
            }

            if (_genericInterfaceMapping.TryGetValue(interfaceType, out var genericInterfaceId))
            {
                return GetAvailableVersions(genericInterfaceId);
            }

            if (GenericGrainInterfaceType.TryParse(interfaceType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericInterfaceMapping[interfaceType] = generic.GetGenericGrainType().Value;
                return GetAvailableVersions(genericId);
            }

            // No versions available.
            return (cache.Version, Array.Empty<ushort>());
        }

        /// <summary>
        /// Gets the set of supported silos for a specified grain interface and version.
        /// </summary>
        /// <param name="interfaceType">The grain interface type name.</param>
        /// <param name="version">The grain interface version.</param>
        /// <returns>The set of silos which support the specified grain interface type and version.</returns>
        public (MajorMinorVersion Version, SiloAddress[] Result) GetSupportedSilos(GrainInterfaceType interfaceType, ushort version)
        {
            var cache = GetCache();
            if (cache.SupportedSilosByInterface.TryGetValue((interfaceType, version), out var result))
            {
                return (cache.Version, result);
            }

            if (_genericInterfaceMapping.TryGetValue(interfaceType, out var genericInterfaceId))
            {
                return GetSupportedSilos(genericInterfaceId, version);
            }

            if (GenericGrainInterfaceType.TryParse(interfaceType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericInterfaceMapping[interfaceType] = generic.GetGenericGrainType().Value;
                return GetSupportedSilos(genericId, version);
            }

            // No supported silos for this version.
            return (cache.Version, Array.Empty<SiloAddress>());
        }

        /// <summary>
        /// Gets the set of supported silos for the specified grain type.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <returns>The silos which support the specified grain type.</returns>
        public (MajorMinorVersion Version, SiloAddress[] Result) GetSupportedSilos(GrainType grainType)
        {
            var cache = GetCache();
            if (cache.SupportedSilosByGrainType.TryGetValue(grainType, out var result))
            {
                return (cache.Version, result);
            }

            if (_genericGrainTypeMapping.TryGetValue(grainType, out var genericGrainType))
            {
                return GetSupportedSilos(genericGrainType);
            }

            if (GenericGrainType.TryParse(grainType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericGrainTypeMapping[grainType] = generic.GetUnconstructedGrainType().GrainType;
                return GetSupportedSilos(genericId);
            }

            // No supported silos for this type.
            return (cache.Version, Array.Empty<SiloAddress>());
        }

        /// <summary>
        /// Gets the set of supported silos for the specified combination of grain type, interface type, and version.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="interfaceType">The grain interface type name.</param>
        /// <param name="versions">The grain interface version.</param>
        /// <returns>The set of silos which support the specified grain.</returns>
        public (MajorMinorVersion Version, Dictionary<ushort, SiloAddress[]> Result) GetSupportedSilos(GrainType grainType, GrainInterfaceType interfaceType, ushort[] versions)
        {
            var result = new Dictionary<ushort, SiloAddress[]>();

            // Track the minimum version in case of inconsistent reads, since the caller can use that information to
            // ensure they refresh on the next call.
            MajorMinorVersion? minCacheVersion = null;
            foreach (var version in versions)
            {
                (var cacheVersion, var silosWithGrain) = this.GetSupportedSilos(grainType);
                if (!minCacheVersion.HasValue || cacheVersion > minCacheVersion.Value)
                {
                    minCacheVersion = cacheVersion;
                }

                // We need to sort this so the list of silos returned will
                // be the same across all silos in the cluster
                SiloAddress[] silosWithCorrectVersion;
                (cacheVersion, silosWithCorrectVersion) = this.GetSupportedSilos(interfaceType, version);

                if (!minCacheVersion.HasValue || cacheVersion > minCacheVersion.Value)
                {
                    minCacheVersion = cacheVersion;
                }

                result[version] = silosWithCorrectVersion
                    .Intersect(silosWithGrain)
                    .OrderBy(addr => addr)
                    .ToArray();
            }

            if (!minCacheVersion.HasValue) minCacheVersion = MajorMinorVersion.Zero;

            return (minCacheVersion.Value, result);
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

        private static Dictionary<GrainInterfaceType, ushort> BuildLocalVersionMap(GrainManifest manifest)
        {
            var result = new Dictionary<GrainInterfaceType, ushort>();
            foreach (var grainInterface in manifest.Interfaces)
            {
                var id = grainInterface.Key;

                if (!grainInterface.Value.Properties.TryGetValue(WellKnownGrainInterfaceProperties.Version, out var versionString)
                    || !ushort.TryParse(versionString, out var version))
                {
                    version = 0;
                }

                result[id] = version;
            }

            return result;
        }

        private static Cache BuildCache(ClusterManifest clusterManifest)
        {
            var available = new Dictionary<GrainInterfaceType, List<ushort>>();
            var supportedInterfaces = new Dictionary<(GrainInterfaceType, ushort), List<SiloAddress>>();
            var supportedGrains = new Dictionary<GrainType, List<SiloAddress>>();

            foreach (var entry in clusterManifest.Silos)
            {
                var silo = entry.Key;

                // Since clients are not eligible for placement, we exclude them here.
                if (silo.IsClient)
                {
                    continue;
                }

                var manifest = entry.Value;
                foreach (var grainInterface in manifest.Interfaces)
                {
                    var id = grainInterface.Key;

                    if (!grainInterface.Value.Properties.TryGetValue(WellKnownGrainInterfaceProperties.Version, out var versionString)
                        || !ushort.TryParse(versionString, out var version))
                    {
                        version = 0;
                    }

                    if (!available.TryGetValue(id, out var versions))
                    {
                        available[id] = new List<ushort> { version };
                    }
                    else if (!versions.Contains(version))
                    {
                        versions.Add(version);
                    }

                    if (!supportedInterfaces.TryGetValue((id, version), out var supportedSilos))
                    {
                        supportedInterfaces[(id, version)] = new List<SiloAddress> { silo };
                    }
                    else if (!supportedSilos.Contains(silo))
                    {
                        supportedSilos.Add(silo);
                    }
                }

                foreach (var grainType in manifest.Grains)
                {
                    var id = grainType.Key;
                    if (!supportedGrains.TryGetValue(id, out var supportedSilos))
                    {
                        supportedGrains[id] = new List<SiloAddress> { silo };
                    }
                    else if (!supportedSilos.Contains(silo))
                    {
                        supportedSilos.Add(silo);
                    }
                }
            }

            var resultAvailable = new Dictionary<GrainInterfaceType, ushort[]>();
            foreach (var entry in available)
            {
                entry.Value.Sort();
                resultAvailable[entry.Key] = entry.Value.ToArray();
            }

            var resultSupportedByInterface = new Dictionary<(GrainInterfaceType, ushort), SiloAddress[]>();
            foreach (var entry in supportedInterfaces)
            {
                entry.Value.Sort();
                resultSupportedByInterface[entry.Key] = entry.Value.ToArray();
            }

            var resultSupportedSilosByGrainType = new Dictionary<GrainType, SiloAddress[]>();
            foreach (var entry in supportedGrains)
            {
                entry.Value.Sort();
                resultSupportedSilosByGrainType[entry.Key] = entry.Value.ToArray();
            }

            return new Cache(clusterManifest.Version, resultAvailable, resultSupportedByInterface, resultSupportedSilosByGrainType);
        }

        private class Cache
        {
            public Cache(
                MajorMinorVersion version,
                Dictionary<GrainInterfaceType, ushort[]> availableVersions,
                Dictionary<(GrainInterfaceType, ushort), SiloAddress[]> supportedSilosByInterface,
                Dictionary<GrainType, SiloAddress[]> supportedSilosByGrainType)
            {
                this.Version = version;
                this.AvailableVersions = availableVersions;
                this.SupportedSilosByGrainType = supportedSilosByGrainType;
                this.SupportedSilosByInterface = supportedSilosByInterface;
            }

            public MajorMinorVersion Version { get; }
            public Dictionary<GrainInterfaceType, ushort[]> AvailableVersions { get; } 
            public Dictionary<(GrainInterfaceType, ushort), SiloAddress[]> SupportedSilosByInterface { get; } = new Dictionary<(GrainInterfaceType, ushort), SiloAddress[]>();
            public Dictionary<GrainType, SiloAddress[]> SupportedSilosByGrainType { get; } = new Dictionary<GrainType, SiloAddress[]>();
        }
    }
}