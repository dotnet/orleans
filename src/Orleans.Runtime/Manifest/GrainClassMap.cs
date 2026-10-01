using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Metadata
{
    /// <summary>
    /// Mapping between <see cref="GrainType"/> and implementing <see cref="Type"/>.
    /// </summary>
    public class GrainClassMap
    {
        private readonly TypeConverter _typeConverter;
        private readonly ImmutableDictionary<GrainType, Type> _types;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainClassMap"/> class.
        /// </summary>
        /// <param name="typeConverter">The type converter.</param>
        /// <param name="classes">
        /// The grain classes with public constructors preserved, as registered by
        /// <see cref="Serialization.Configuration.TypeManifestOptions.AddInterfaceImplementation(Type)"/>.
        /// </param>
        public GrainClassMap(TypeConverter typeConverter, ImmutableDictionary<GrainType, Type> classes)
        {
            _typeConverter = typeConverter;
            _types = classes;
        }

        /// <summary>
        /// Returns the grain class type corresponding to the provided grain type.
        /// </summary>
        /// <param name="grainType">Type of the grain.</param>
        /// <param name="grainClass">The grain class with its public constructors preserved for activation.</param>
        /// <returns><see langword="true"/> if a corresponding grain class was found, <see langword="false"/> otherwise.</returns>
        public bool TryGetGrainClass(
            GrainType grainType,
            [NotNullWhen(true), DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] out Type? grainClass)
        {
            GrainType lookupType;
            Type[]? args;
            if (GenericGrainType.TryParse(grainType, out var genericId))
            {
                lookupType = genericId.GetUnconstructedGrainType().GrainType;
                args = genericId.GetArguments(_typeConverter);
            }
            else
            {
                lookupType = grainType;
                args = default;
            }

            if (!TryGetRegisteredGrainClass(lookupType, out var registeredClass))
            {
                grainClass = null;
                return false;
            }

            grainClass = args is null ? registeredClass : MakeGenericGrainClass(registeredClass, args);

            return true;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2067",
            Justification = "Generated grain classes and trim-safe manual registrations use TypeManifestOptions.AddInterfaceImplementation, which preserves public constructors. GrainTypeOptions.Classes and the immutable dictionary retain those types but cannot carry their annotations.")]
        private bool TryGetRegisteredGrainClass(
            GrainType grainType,
            [NotNullWhen(true), DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] out Type? grainClass)
            => _types.TryGetValue(grainType, out grainClass);

        [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        private static Type MakeGenericGrainClass(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type grainClass,
            Type[] arguments)
            => grainClass.MakeGenericType(arguments);
    }
}
