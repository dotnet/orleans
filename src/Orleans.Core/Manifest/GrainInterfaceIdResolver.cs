using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.Metadata
{
    /// <summary>
    /// Associates a <see cref="GrainInterfaceId"/> with a <see cref="Type" />.
    /// </summary>
    public class GrainInterfaceIdResolver
    {
        private readonly IGrainInterfaceIdProvider[] _providers;
        private readonly TypeConverter _typeConverter;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceIdResolver"/> instance.
        /// </summary>
        public GrainInterfaceIdResolver(
            IEnumerable<IGrainInterfaceIdProvider> providers,
            TypeConverter typeConverter)
        {
            _providers = providers.ToArray();
            _typeConverter = typeConverter;
        }

        /// <summary>
        /// Returns the <see cref="GrainInterfaceId"/> for the provided interface.
        /// </summary>
        /// <param name="type">The grain interface.</param>
        /// <returns>The <see cref="GrainInterfaceId"/> for the provided interface.</returns>
        public GrainInterfaceId GetGrainInterfaceId(Type type)
        {
            if (!type.IsInterface)
            {
                throw new ArgumentException($"Argument {nameof(type)} must be an interface. Provided value, \"{type}\", is not an interface.", nameof(type));
            }

            // Configured providers take precedence
            foreach (var provider in this._providers)
            {
                if (provider.TryGetGrainInterfaceId(type, out var interfaceId))
                {
                    interfaceId = AddGenericParameters(interfaceId, type);
                    return interfaceId;
                }
            }

            // Conventions are used as a fallback.
            return GetGrainInterfaceIdByConvention(type);
        }

        public GrainInterfaceId GetGrainInterfaceIdByConvention(Type type)
        {
            var result = GrainInterfaceId.Create(_typeConverter.Format(type, DropOuterAssemblyQualification));
            result = AddGenericParameters(result, type);
            return result;

            static TypeSpec DropOuterAssemblyQualification(TypeSpec input) => input switch
            {
                AssemblyQualifiedTypeSpec asm => asm.Type,
                _ => input
            };
        }

        private GrainInterfaceId AddGenericParameters(GrainInterfaceId result, Type type)
        {
            if (GenericGrainInterfaceId.TryParse(result, out var genericGrainType)
                && type.IsConstructedGenericType
                && !type.ContainsGenericParameters
                && !genericGrainType.IsConstructed)
            {
                result = genericGrainType.Construct(_typeConverter, type.GetGenericArguments()).Value;
            }

            return result;
        }
    }
}
