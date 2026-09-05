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
    /// <remarks>
    /// Initializes a new instance of the <see cref="GrainInterfaceTypeResolver"/> class.
    /// </remarks>
    /// <param name="providers">
    /// The collection of grain interface type providers.
    /// </param>
    /// <param name="typeConverter">
    /// The type converter, used for generic parameter names.
    /// </param>
    public class GrainInterfaceTypeResolver(
        IEnumerable<IGrainInterfaceTypeProvider> providers,
        TypeConverter typeConverter)
    {
        private readonly IGrainInterfaceTypeProvider[] _providers = providers.ToArray();

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

        /// <summary>
        /// Gets a grain interface type based upon the default conventions.
        /// </summary>
        /// <param name="type">The grain interface type.</param>
        /// <returns>The grain interface type name.</returns>
        public GrainInterfaceType GetGrainInterfaceTypeByConvention(Type type)
        {
            var result = GrainInterfaceType.Create(typeConverter.Format(type, input => input switch
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
                result = genericGrainType.Construct(typeConverter, type.GetGenericArguments()).Value;
            }

            return result;
        }
    }
}
