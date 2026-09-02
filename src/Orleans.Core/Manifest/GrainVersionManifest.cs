using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Orleans.Metadata;

namespace Orleans.Runtime.Versions
{
    /// <summary>
    /// Functionality for querying the declared version of grain interfaces.
    /// </summary>
    internal sealed class GrainVersionManifest
    {
#if NET9_0_OR_GREATER
        private readonly Lock _lockObj = new();
#else
        private readonly object _lockObj = new();
#endif
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
            var snapshot = Capture();
            return (snapshot.Version, snapshot.GetAvailableVersions(interfaceType));
        }

        /// <summary>
        /// Gets the set of supported silos for a specified grain interface and version.
        /// </summary>
        /// <param name="interfaceType">The grain interface type name.</param>
        /// <param name="version">The grain interface version.</param>
        /// <returns>The set of silos which support the specified grain interface type and version.</returns>
        public (MajorMinorVersion Version, SiloAddress[] Result) GetSupportedSilos(GrainInterfaceType interfaceType, ushort version)
        {
            var snapshot = Capture();
            return (snapshot.Version, snapshot.GetSupportedSilos(interfaceType, version));
        }

        /// <summary>
        /// Gets the set of supported silos for the specified grain type.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <returns>The silos which support the specified grain type.</returns>
        public (MajorMinorVersion Version, SiloAddress[] Result) GetSupportedSilos(GrainType grainType)
        {
            var snapshot = Capture();
            return (snapshot.Version, snapshot.GetSupportedSilos(grainType));
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
            var snapshot = Capture();
            return (snapshot.Version, snapshot.GetSupportedSilos(grainType, interfaceType, versions));
        }

        internal Snapshot Capture() => new(this, GetCache());

        private ushort[] GetAvailableVersions(Cache cache, GrainInterfaceType interfaceType)
        {
            if (cache.AvailableVersions.TryGetValue(interfaceType, out var result))
            {
                return result;
            }

            if (_genericInterfaceMapping.TryGetValue(interfaceType, out var genericInterfaceId))
            {
                return GetAvailableVersions(cache, genericInterfaceId);
            }

            if (GenericGrainInterfaceType.TryParse(interfaceType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericInterfaceMapping[interfaceType] = generic.GetGenericGrainType().Value;
                return GetAvailableVersions(cache, genericId);
            }

            return Array.Empty<ushort>();
        }

        private SiloAddress[] GetSupportedSilos(Cache cache, GrainInterfaceType interfaceType, ushort version)
        {
            if (cache.SupportedSilosByInterface.TryGetValue((interfaceType, version), out var result))
            {
                return result;
            }

            if (_genericInterfaceMapping.TryGetValue(interfaceType, out var genericInterfaceId))
            {
                return GetSupportedSilos(cache, genericInterfaceId, version);
            }

            if (GenericGrainInterfaceType.TryParse(interfaceType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericInterfaceMapping[interfaceType] = generic.GetGenericGrainType().Value;
                return GetSupportedSilos(cache, genericId, version);
            }

            return Array.Empty<SiloAddress>();
        }

        private SiloAddress[] GetSupportedSilos(Cache cache, GrainType grainType)
        {
            if (cache.SupportedSilosByGrainType.TryGetValue(grainType, out var result))
            {
                return result;
            }

            if (_genericGrainTypeMapping.TryGetValue(grainType, out var genericGrainType))
            {
                return GetSupportedSilos(cache, genericGrainType);
            }

            if (GenericGrainType.TryParse(grainType, out var generic) && generic.IsConstructed)
            {
                var genericId = _genericGrainTypeMapping[grainType] = generic.GetUnconstructedGrainType().GrainType;
                return GetSupportedSilos(cache, genericId);
            }

            return Array.Empty<SiloAddress>();
        }

        private Cache GetCache()
        {
            var cache = Volatile.Read(ref _cache);
            var manifest = _clusterManifestProvider.Current;
            if (manifest.Version == cache.Version)
            {
                return cache;
            }

            lock (_lockObj)
            {
                cache = Volatile.Read(ref _cache);
                manifest = _clusterManifestProvider.Current;
                if (manifest.Version == cache.Version)
                {
                    return cache;
                }

                cache = BuildCache(manifest);
                Volatile.Write(ref _cache, cache);
                return cache;
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
                    else
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
                    else
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

        internal readonly struct Snapshot
        {
            private readonly GrainVersionManifest _owner;
            private readonly Cache _cache;

            internal Snapshot(GrainVersionManifest owner, Cache cache)
            {
                _owner = owner;
                _cache = cache;
            }

            public MajorMinorVersion Version => _cache.Version;

            public ushort[] GetAvailableVersions(GrainInterfaceType interfaceType) =>
                _owner.GetAvailableVersions(_cache, interfaceType);

            public SiloAddress[] GetSupportedSilos(GrainInterfaceType interfaceType, ushort version) =>
                _owner.GetSupportedSilos(_cache, interfaceType, version);

            public SiloAddress[] GetSupportedSilos(GrainType grainType) =>
                _owner.GetSupportedSilos(_cache, grainType);

            public Dictionary<ushort, SiloAddress[]> GetSupportedSilos(
                GrainType grainType,
                GrainInterfaceType interfaceType,
                ushort[] versions) =>
                GetSupportedSilos(grainType, interfaceType, versions, out _);

            public Dictionary<ushort, SiloAddress[]> GetSupportedSilos(
                GrainType grainType,
                GrainInterfaceType interfaceType,
                ushort[] versions,
                out SiloAddress[] allSupportedSilos)
            {
                var result = new Dictionary<ushort, SiloAddress[]>(versions.Length);
                var silosWithGrain = GetSupportedSilos(grainType);
                allSupportedSilos = Array.Empty<SiloAddress>();
                foreach (var version in versions)
                {
                    var supportedSilos = IntersectSorted(
                        silosWithGrain,
                        GetSupportedSilos(interfaceType, version));
                    result[version] = supportedSilos;
                    allSupportedSilos = UnionSorted(allSupportedSilos, supportedSilos);
                }

                return result;
            }

            private static SiloAddress[] UnionSorted(SiloAddress[] left, SiloAddress[] right)
            {
                if (left.Length == 0)
                {
                    return right;
                }

                if (right.Length == 0)
                {
                    return left;
                }

                var result = new SiloAddress[left.Length + right.Length];
                var leftIndex = 0;
                var rightIndex = 0;
                var resultIndex = 0;
                while (leftIndex < left.Length && rightIndex < right.Length)
                {
                    var comparison = left[leftIndex].CompareTo(right[rightIndex]);
                    if (comparison < 0)
                    {
                        result[resultIndex++] = left[leftIndex++];
                    }
                    else if (comparison > 0)
                    {
                        result[resultIndex++] = right[rightIndex++];
                    }
                    else
                    {
                        result[resultIndex++] = left[leftIndex++];
                        ++rightIndex;
                    }
                }

                while (leftIndex < left.Length)
                {
                    result[resultIndex++] = left[leftIndex++];
                }

                while (rightIndex < right.Length)
                {
                    result[resultIndex++] = right[rightIndex++];
                }

                if (resultIndex != result.Length)
                {
                    Array.Resize(ref result, resultIndex);
                }

                return result;
            }

            private static SiloAddress[] IntersectSorted(SiloAddress[] left, SiloAddress[] right)
            {
                if (left.Length == 0 || right.Length == 0)
                {
                    return Array.Empty<SiloAddress>();
                }

                var result = new SiloAddress[Math.Min(left.Length, right.Length)];
                var leftIndex = 0;
                var rightIndex = 0;
                var resultIndex = 0;
                while (leftIndex < left.Length && rightIndex < right.Length)
                {
                    var comparison = left[leftIndex].CompareTo(right[rightIndex]);
                    if (comparison < 0)
                    {
                        ++leftIndex;
                    }
                    else if (comparison > 0)
                    {
                        ++rightIndex;
                    }
                    else
                    {
                        result[resultIndex++] = left[leftIndex];
                        ++leftIndex;
                        ++rightIndex;
                    }
                }

                if (resultIndex == result.Length)
                {
                    return result;
                }

                Array.Resize(ref result, resultIndex);
                return result;
            }
        }

        internal sealed class Cache
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
            public Dictionary<(GrainInterfaceType, ushort), SiloAddress[]> SupportedSilosByInterface { get; }
            public Dictionary<GrainType, SiloAddress[]> SupportedSilosByGrainType { get; }
        }
    }
}