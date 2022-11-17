using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Serialization.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.TypeSystem;
using Orleans.Core;
using System.Diagnostics.Metrics;
using Orleans.Metadata;
using GrainTypeResolver = Orleans.Metadata.GrainTypeResolver;

namespace Orleans.Persistence.Migration
{
    public interface IGrainReferenceExtractor
    {
        (GrainType grainType, GrainInterfaceType grainInterfaceType, IdSpan key) Extract(GrainReference grainReference);
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
        private readonly GrainTypeManager _grainTypeManager;
        private readonly GrainTypeResolver _grainTypeResolver;
        private readonly GrainInterfaceTypeResolver _grainInterfaceTypeResolver;

        public GrainReferenceExtractor(
            GrainTypeManager grainTypeManager,
            GrainTypeResolver grainTypeResolver,
            GrainInterfaceTypeResolver grainInterfaceTypeResolver)
        {
            _grainTypeManager = grainTypeManager;
            _grainTypeResolver = grainTypeResolver;
            _grainInterfaceTypeResolver = grainInterfaceTypeResolver;
        }

        public (GrainType grainType, GrainInterfaceType grainInterfaceType, IdSpan key) Extract(GrainReference grainReference)
        {
            // Get GrainType
            var typeCode = grainReference.GrainIdentity.TypeCode;
            _grainTypeManager.GetTypeInfo(typeCode, out var grainClass, out _, grainReference.GenericArguments);
            Type grainType = LookupType(grainClass) ?? throw new ArgumentException("Grain type not found");
            var type = _grainTypeResolver.GetGrainType(grainType);

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
            var interfaceType = _grainInterfaceTypeResolver.GetGrainInterfaceType(iface);

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
    }
}
