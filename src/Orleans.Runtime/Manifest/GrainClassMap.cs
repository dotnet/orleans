using System;
using System.Collections.Immutable;
using System.Threading;
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
        private ImmutableDictionary<GrainType, Type> _types;

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
        /// Replaces the grain class mapping after a hot reload update; this shared singleton is registered
        /// separately from the manifest provider, so it is updated in place rather than replaced.
        /// </summary>
        internal void OnManifestUpdated(ImmutableDictionary<GrainType, Type> classes) => Volatile.Write(ref _types, classes);

        /// <summary>
        /// Returns the grain class type corresponding to the provided grain type.
        /// </summary>
        /// <param name="grainType">Type of the grain.</param>
        /// <param name="grainClass">The grain class.</param>
        /// <returns><see langword="true"/> if a corresponding grain class was found, <see langword="false"/> otherwise.</returns>
        public bool TryGetGrainClass(GrainType grainType, [NotNullWhen(true)] out Type? grainClass)
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

            if (!Volatile.Read(ref _types).TryGetValue(lookupType, out grainClass))
            {
                return false;
            }

            if (args is not null)
            {
                grainClass = grainClass.MakeGenericType(args);
            }

            return true;
        }
    }
}
