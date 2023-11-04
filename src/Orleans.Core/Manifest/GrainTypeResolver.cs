using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Metadata
{
    /// <summary>
    /// Associates a <see cref="GrainType"/> with a grain class.
    /// </summary>
    public class GrainTypeResolver
    {
        private const string GrainSuffix = "grain";
        private readonly IGrainTypeProvider[] _providers;
        private readonly TypeConverter _typeConverter;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainTypeResolver"/> class.
        /// </summary>
        /// <param name="resolvers">
        /// The grain type name providers.
        /// </param>
        /// <param name="argumentFormatter">
        /// The type converter, used to format generic parameters.
        /// </param>
        public GrainTypeResolver(
            IEnumerable<IGrainTypeProvider> resolvers,
            TypeConverter argumentFormatter)
        {
            _providers = resolvers.ToArray();
            _typeConverter = argumentFormatter;
        }

        /// <summary>
        /// Returns the grain type for the provided class.
        /// </summary>
        /// <param name="type">The grain class.</param>
        /// <returns>The grain type for the provided class.</returns>
        public GrainType GetGrainType(Type type)
        {
            if (!type.IsClass || type.IsAbstract)
            {
                throw new ArgumentException($"Argument {nameof(type)} must be a non-abstract class. Provided value, \"{type}\", is not a class.", nameof(type));
            }

            // Configured providers take precedence
            foreach (var provider in _providers)
            {
                if (provider.TryGetGrainType(type, out var grainType))
                {
                    grainType = AddGenericParameters(grainType, type);

                    return grainType;
                }
            }

            // Conventions are used as a fallback
            return GetGrainTypeByConvention(type);
        }

        private GrainType GetGrainTypeByConvention(Type type)
        {
            var name = type.Name.ToLowerInvariant();

            // Trim generic arity
            var index = name.IndexOf('`');
            if (index > 0)
            {
                name = name[..index];
            }

            // Trim "Grain" suffix
            index = name.LastIndexOf(GrainSuffix);
            if (index > 0 && name.Length - index == GrainSuffix.Length)
            {
                name = name[..index];
            }

            // Append the generic arity, eg typeof(MyListGrain<T>) would eventually become mylist`1
            if (type.IsGenericType)
            {
                name = name + '`' + type.GetGenericArguments().Length;
            }

            var grainType = GrainType.Create(name);
            grainType = AddGenericParameters(grainType, type);
            return grainType;
        }

        private GrainType AddGenericParameters(GrainType grainType, Type type)
        {
            if (GenericGrainType.TryParse(grainType, out var genericGrainType)
                && type.IsConstructedGenericType
                && !type.ContainsGenericParameters
                && !genericGrainType.IsConstructed)
            {
                var typeArguments = type.GetGenericArguments();
                grainType = genericGrainType.Construct(_typeConverter, typeArguments).GrainType;
            }

            return grainType;
        }
    }
}
