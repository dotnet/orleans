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
        /// <param name="classes">The grain classes.</param>
        public GrainClassMap(TypeConverter typeConverter, ImmutableDictionary<GrainType, Type> classes)
        {
            _typeConverter = typeConverter;
            _types = classes;
        }

        /// <summary>
        /// Returns the grain class type corresponding to the provided grain type.
        /// </summary>
        /// <param name="grainType">Type of the grain.</param>
        /// <param name="grainClass">The grain class.</param>
        /// <returns><see langword="true"/> if a corresponding grain class was found, <see langword="false"/> otherwise.</returns>
        public bool TryGetGrainClass(GrainType grainType, [NotNullWhen(true)] out Type grainClass)
        {
            GrainType lookupType;
            Type[] args;
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

            if (!_types.TryGetValue(lookupType, out grainClass))
            {
                return false;
            }

            if (args is object)
            {
                grainClass = grainClass.MakeGenericType(args);
            }

            return true;
        }
    }
}
