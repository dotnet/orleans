using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Metadata
{
    /// <summary>
    /// Associates a <see cref="GrainInterfaceType"/> with a <see cref="Type" />.
    /// </summary>
    public class GrainInterfaceTypeResolver
    {
        private readonly IGrainInterfaceTypeProvider[] _providers;
        private readonly TypeConverter _typeConverter;

        /// <summary>
        /// Creates a <see cref="GrainInterfaceTypeResolver"/> instance.
        /// </summary>
        public GrainInterfaceTypeResolver(
            IEnumerable<IGrainInterfaceTypeProvider> providers,
            TypeConverter typeConverter)
        {
            _providers = providers.ToArray();
            _typeConverter = typeConverter;
        }

        /// <summary>
        /// Returns the <see cref="GrainInterfaceType"/> for the provided interface.
        /// </summary>
        /// <param name="type">The grain interface.</param>
        /// <returns>The <see cref="GrainInterfaceType"/> for the provided interface.</returns>
        public GrainInterfaceType GetGrainInterfaceType(Type type)
        {
            if (!type.IsInterface)
            {
                throw new ArgumentException($"Argument {nameof(type)} must be an interface. Provided value, \"{type}\", is not an interface.", nameof(type));
            }

            // Configured providers take precedence
            foreach (var provider in _providers)
            {
                if (provider.TryGetGrainInterfaceType(type, out var interfaceType))
                {
                    interfaceType = AddGenericParameters(interfaceType, type);
                    return interfaceType;
                }
            }

            // Conventions are used as a fallback.
            return GetGrainInterfaceTypeByConvention(type);
        }

        public GrainInterfaceType GetGrainInterfaceTypeByConvention(Type type)
        {
            var result = GrainInterfaceType.Create(_typeConverter.Format(type, input => input switch
            {
                AssemblyQualifiedTypeSpec asm => asm.Type, // drop outer assembly qualification
                _ => input
            }));

            result = AddGenericParameters(result, type);
            return result;
        }

        private GrainInterfaceType AddGenericParameters(GrainInterfaceType result, Type type)
        {
            if (GenericGrainInterfaceType.TryParse(result, out var genericGrainType)
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
