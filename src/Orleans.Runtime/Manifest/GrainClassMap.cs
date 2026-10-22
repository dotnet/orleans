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
        /// <see cref="Configuration.GrainTypeOptions.AddClass(Type)"/> or
        /// <see cref="Serialization.Configuration.TypeManifestOptions.AddInterfaceImplementation(Type)"/>.
        /// </param>
        [RequiresUnreferencedCode("The dictionary's grain types must have public constructors preserved separately, for example by GrainTypeOptions.AddClass or TypeManifestOptions.AddInterfaceImplementation.")]
        public GrainClassMap(TypeConverter typeConverter, ImmutableDictionary<GrainType, Type> classes)
        {
            _typeConverter = typeConverter;
            _types = classes;
        }

        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "SiloManifestProvider builds this dictionary from GrainTypeOptions registrations. Generated manifests preserve constructors through AddInterfaceImplementation and manual registrations through AddClass. Direct Classes access warns callers to preserve constructors separately.")]
        internal static GrainClassMap CreateRegistered(TypeConverter typeConverter, ImmutableDictionary<GrainType, Type> classes)
            => new(typeConverter, classes);

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
            Justification = "Generated and manual registrations preserve public constructors through TypeManifestOptions.AddInterfaceImplementation or GrainTypeOptions.AddClass. Direct Classes access and the public dictionary constructor require callers to preserve constructors separately and warn when trimming. The collections retain those types but cannot carry their annotations.")]
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
