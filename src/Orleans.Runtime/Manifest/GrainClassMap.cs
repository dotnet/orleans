using System;
using System.Collections.Immutable;
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

        public GrainClassMap(TypeConverter typeConverter, ImmutableDictionary<GrainType, Type> classes)
        {
            _typeConverter = typeConverter;
            _types = classes;
        }

        /// <summary>
        /// Returns the grain class type corresponding to the provided grain type.
        /// </summary>
        public bool TryGetGrainClass(GrainType grainType, out Type grainClass)
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
