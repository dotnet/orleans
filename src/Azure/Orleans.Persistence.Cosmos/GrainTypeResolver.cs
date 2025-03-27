using System.Reflection;

namespace Orleans.Persistence.Cosmos
{
    internal class GrainTypeResolver
    {
        private const string GrainSuffix = "grain";

        public static string GetGrainTypeByConvention(Type grainClass, bool? forceGrainTypeAttribute = false)
        {
            var grainTypeAttr = grainClass.GetCustomAttribute<GrainTypeAttribute>();
            if (grainTypeAttr is not null)
            {
                return grainTypeAttr.GrainType;
            }
            if (forceGrainTypeAttribute == true && grainTypeAttr is null)
            {
                throw new InvalidOperationException($"All grain classes must specify a grain type name using the [GrainType(type)] attribute. Grain class '{grainClass}' does not.");
            }

            // use default convention to extract grainTypeName here:
            var name = grainClass.Name.ToLowerInvariant();

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
            if (grainClass.IsGenericType)
            {
                name = name + '`' + grainClass.GetGenericArguments().Length;
            }

            return name;
        }
    }
}
