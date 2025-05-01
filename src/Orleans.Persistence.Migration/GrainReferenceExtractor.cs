using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Metadata;
using Orleans.Runtime;
using GrainTypeResolver = Orleans.Metadata.GrainTypeResolver;

namespace Orleans.Persistence.Migration
{
    public interface IGrainReferenceExtractor
    {
        GrainReference ResolveGrainReference(string grainId);
        GrainReference ResolveGrainReference(string grainType, string keyStr);
        Type ResolveInterfaceType(string grainType, string keyStr, string interfaceTypeName);

        (GrainType grainType, GrainInterfaceType grainInterfaceType, IdSpan key) Extract(GrainReference grainReference);

        Type ExtractType(GrainReference grainReference);
    }

    public static class GrainReferenceExtractorExtension
    {
        public static string GetGrainId(this IGrainReferenceExtractor @this, GrainReference grainReference)
        {
            var (type, _, key) = @this.Extract(grainReference);
            return $"{type}/{key}";
        }
    }

    internal class GrainReferenceExtractor : IGrainReferenceExtractor
    {
        private readonly ILogger _logger;

        private readonly GrainTypeManager _grainTypeManager;
        private readonly GrainTypeResolver _grainTypeResolver;
        private readonly GrainInterfaceTypeResolver _grainInterfaceTypeResolver;
        private readonly IGrainReferenceRuntime _grainReferenceRuntime;

        private readonly ConcurrentDictionary<string, int> _grainTypeTypeCodeMap;
        private readonly ConcurrentDictionary<string, int> _interfaceTypeIdMap;

        public GrainReferenceExtractor(
            ILoggerFactory loggerFactory,
            GrainTypeManager grainTypeManager,
            GrainTypeResolver grainTypeResolver,
            GrainInterfaceTypeResolver grainInterfaceTypeResolver,
            IGrainReferenceRuntime grainReferenceRuntime)
        {
            _logger = loggerFactory.CreateLogger<GrainReferenceExtractor>();
            _grainTypeManager = grainTypeManager;
            _grainTypeResolver = grainTypeResolver;
            _grainInterfaceTypeResolver = grainInterfaceTypeResolver;
            _grainReferenceRuntime = grainReferenceRuntime;

            _grainTypeTypeCodeMap = new();
            InitializeGrainTypeTypeCodeMap();

            _interfaceTypeIdMap = new();
            InitializeInterfaceTypeIdMap();
        }

        void InitializeGrainTypeTypeCodeMap()
        {
            // any grain type change should be reflected in _grainTypeTypeCodeMap
            _grainTypeManager.ClusterGrainInterfaceMap.onGrainTypeAdded += (type, typeCode) => TryAddGrainTypeTypeCode(type, typeCode);

            var snapshot = new Dictionary<Type, int>(_grainTypeManager.ClusterGrainInterfaceMap.grainTypeTypeCodeMap);
            foreach (var (type, typeCode) in snapshot)
            {
                TryAddGrainTypeTypeCode(type, typeCode);
            }
        }

        void InitializeInterfaceTypeIdMap()
        {
            // any grain type change should be reflected in _grainTypeTypeCodeMap
            _grainTypeManager.ClusterGrainInterfaceMap.onGrainInterfaceAdded += (data) => TryAddInterfaceTypeId(data);

            var snapshot = new List<GrainInterfaceData>(_grainTypeManager.ClusterGrainInterfaceMap.GrainInterfaceDatas);
            foreach (var data in snapshot)
            {
                TryAddInterfaceTypeId(data);
            }
        }

        public Type ExtractType(GrainReference grainReference)
        {
            var typeCode = grainReference.GrainIdentity.TypeCode;
            _grainTypeManager.GetTypeInfo(typeCode, out var grainClass, out _, grainReference.GenericArguments);
            Type grainType = LookupType(grainClass) ?? throw new ArgumentException("Grain type not found");
            return grainType;
        }

        /// <summary>
        /// Does a reverse lookup of the grain reference from the grain id.
        /// grainId should be in format 'grainType/grainId'.
        /// </summary>
        public GrainReference ResolveGrainReference(string grainId)
        {
            var parts = grainId.Split('/', 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException("Invalid grainId format. Expected 'grainType/grainId'.");
            }

            return ResolveGrainReference(parts[0], parts[1]);
        }

        /// <summary>
        /// Does a reverse lookup of the grain reference from the grain id (coded as grainType and key).
        /// </summary>
        public GrainReference ResolveGrainReference(string grainType, string keyStr)
        {
            if (!_grainTypeTypeCodeMap.TryGetValue(grainType, out var typeCode))
            {
                throw new ArgumentException($"Grain type '{grainType}' not found.");
            }

            GrainId grainId;
            if (GrainIdKeyExtensions.TryParseGuidKey(keyStr, out var guidKey, out var ext))
            {
                grainId = GrainId.GetGrainId(typeCode: typeCode, primaryKey: guidKey, ext);
            }
            else if (GrainIdKeyExtensions.TryParseLongKey(keyStr, out var longKey, out ext))
            {
                grainId = GrainId.GetGrainId(typeCode: typeCode, primaryKey: longKey, ext);
            }
            else
            {
                var key = GrainIdKeyExtensions.ParseStringKey(keyStr);
                grainId = GrainId.GetGrainId(typeCode: typeCode, primaryKey: key);
            }

            return GrainReference.FromGrainId(grainId, _grainReferenceRuntime);
        }

        public Type ResolveInterfaceType(string grainType, string keyStr, string interfaceTypeName)
        {
            if (!_interfaceTypeIdMap.TryGetValue(interfaceTypeName, out var interfaceId))
            {
                throw new ArgumentException($"Interface type '{interfaceTypeName}' not found.");
            }
            if (!_grainTypeManager.ClusterGrainInterfaceMap.TryGetServiceInterface(interfaceId, out var iface))
            {
                throw new ArgumentException($"Interface type '{interfaceTypeName}' not found at {nameof(_grainTypeManager.ClusterGrainInterfaceMap)}.");
            }

            if (iface.IsGenericType)
            {
                // TODO double-check - how could we do it?
                throw new NotSupportedException($"Resolving generic interface type '{interfaceTypeName}' is not supported.");
            }

            return iface;
        }

        public (GrainType grainType, GrainInterfaceType grainInterfaceType, IdSpan key) Extract(GrainReference grainReference)
        {
            // Get GrainType
            var typeCode = grainReference.GrainIdentity.TypeCode;
            _grainTypeManager.GetTypeInfo(typeCode, out var grainClass, out _, grainReference.GenericArguments);
            Type grainType = LookupType(grainClass) ?? throw new ArgumentException("Grain type not found");
            var type = _grainTypeResolver.GetGrainType(grainType);

            GrainInterfaceType interfaceType;
            try
            {
                // Get GrainInterfaceType
                Type iface = null;
                if (_grainTypeManager.GrainTypeResolver.TryGetInterfaceData(grainReference.InterfaceId, out var interfaceData))
                {
                    if (interfaceData.Interface.IsGenericType)
                    {
                        // We cannot use grainReference.InterfaceName because it doesn't match
                        foreach (var candidate in grainType.GetInterfaces())
                        {
                            if (candidate.Name.Equals(interfaceData.Interface.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                iface = candidate;
                                break;
                            }
                        }
                    }
                    else
                    {
                        iface = interfaceData.Interface;
                    }
                }
                if (iface == null)
                {
                    throw new ArgumentException("Grain interface type not found");
                }
                interfaceType = _grainInterfaceTypeResolver.GetGrainInterfaceType(iface);
            }
            catch (InvalidOperationException)
            {
                // The GrainReference doesn't include the interface. It's fine, ignore it and let interfaceType to be blank
                interfaceType = default;
            }

            // Extract Key
            IdSpan key;
            if (grainReference.IsPrimaryKeyBasedOnLong())
            {
                var keyBase = grainReference.GetPrimaryKeyLong(out var keyExt);
                // Check if it is a string key
                key = keyBase == 0 && grainReference.GetType().IsAssignableTo(typeof(IGrainWithStringKey))
                    ? IdSpan.Create(keyExt)
                    : GrainIdKeyExtensions.CreateIntegerKey(keyBase, keyExt);
            }
            else
            {
                var keyBase = grainReference.GetPrimaryKey(out var keyExt);
                key = GrainIdKeyExtensions.CreateGuidKey(keyBase, keyExt);
            }

            return (type, interfaceType, key);
        }

        private static Type LookupType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var grainType = assembly.GetType(typeName);
                if (grainType != null)
                {
                    return grainType;
                }
            }
            return null;
        }

        void TryAddInterfaceTypeId(GrainInterfaceData data)
        {
            try
            {
                var interfaceType = _grainInterfaceTypeResolver.GetGrainInterfaceType(data.Interface);
                _interfaceTypeIdMap.TryAdd(interfaceType, data.InterfaceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning((int)MigrationErrorCode.InterfaceTypeResolveError, $"Failed to resolve interface type '{data.Interface?.ToString()}' ", ex);
            }
        }

        void TryAddGrainTypeTypeCode(Type type, int typeCode, bool throwOnError = false)
        {
            try
            {
                var grainType = _grainTypeResolver.GetGrainType(type);
                _grainTypeTypeCodeMap.TryAdd(grainType.ToString(), typeCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning((int)MigrationErrorCode.GrainTypeResolveError, $"Failed to resolve grainType '{type}' ", ex);
                if (throwOnError)
                {
                    throw;
                }
            }
        }
    }
}
