using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
    internal interface IGrainTypeResolver
    {
        bool TryGetGrainClassData(Type grainInterfaceType, out GrainClassData implementation, string grainClassNamePrefix);
        bool IsUnordered(int grainTypeCode);
    }

    [Serializable]
    internal class GrainTypeResolver : IGrainTypeResolver
    {
        private readonly Dictionary<string, GrainInterfaceData> typeToInterfaceData;

#pragma warning disable IDE0051 // Remove unused private members
#pragma warning disable CS0169 // Remove unused private members
        // Unused. Retained for serializer compatibility.
        private readonly Dictionary<int, GrainInterfaceData> table;

        // Unused. Retained for serializer compatibility.
        private readonly HashSet<string> loadedGrainAsemblies;
#pragma warning restore CS0169 // Remove unused private members
#pragma warning restore IDE0051 // Remove unused private members

        private readonly HashSet<int> unordered;

        public GrainTypeResolver(
            Dictionary<string, GrainInterfaceData> typeToInterfaceData,
            HashSet<int> unordered)
        {
            this.typeToInterfaceData = typeToInterfaceData;
            this.unordered = unordered;
        }

        public bool TryGetGrainClassData(Type interfaceType, out GrainClassData implementation, string grainClassNamePrefix)
        {
            implementation = null;
            GrainInterfaceData interfaceData;

            // First, try to find a non-generic grain implementation:
            if (this.typeToInterfaceData.TryGetValue(GrainInterfaceMap.GetTypeKey(interfaceType, false), out interfaceData) &&
                TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix))
            {
                return true;
            }

            // If a concrete implementation was not found and the interface is generic, 
            // try to find a generic grain implementation:
            if (interfaceType.IsGenericType &&
                this.typeToInterfaceData.TryGetValue(GrainInterfaceMap.GetTypeKey(interfaceType, true), out interfaceData) &&
                TryGetGrainClassData(interfaceData, out implementation, grainClassNamePrefix))
            {
                return true;
            }

            return false;
        }

        public bool IsUnordered(int grainTypeCode)
        {
            return unordered.Contains(grainTypeCode);
        }

        private static bool TryGetGrainClassData(GrainInterfaceData interfaceData, out GrainClassData implementation, string grainClassNamePrefix)
        {
            implementation = null;
            var implementations = interfaceData.Implementations;

            if (implementations.Length == 0)
                return false;

            if (String.IsNullOrEmpty(grainClassNamePrefix))
            {
                if (implementations.Length == 1)
                {
                    implementation = implementations[0];
                    return true;
                }

                if (interfaceData.PrimaryImplementation != null)
                {
                    implementation = interfaceData.PrimaryImplementation;
                    return true;
                }

                throw new OrleansException(String.Format("Cannot resolve grain interface ID={0} to a grain class because of multiple implementations of it: {1}",
                    interfaceData.InterfaceId, Utils.EnumerableToString(implementations, d => d.GrainClass, ",", false)));
            }

            if (implementations.Length == 1)
            {
                if (implementations[0].GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal))
                {
                    implementation = implementations[0];
                    return true;
                }

                return false;
            }

            var matches = implementations.Where(impl => impl.GrainClass.Equals(grainClassNamePrefix)).ToArray(); //exact match?
            if (matches.Length == 0)
                matches = implementations.Where(
                    impl => impl.GrainClass.StartsWith(grainClassNamePrefix, StringComparison.Ordinal)).ToArray(); //prefix matches

            if (matches.Length == 0)
                return false;

            if (matches.Length == 1)
            {
                implementation = matches[0];
                return true;
            }

            throw new OrleansException(String.Format("Cannot resolve grain interface ID={0}, grainClassNamePrefix={1} to a grain class because of multiple implementations of it: {2}",
                interfaceData.InterfaceId,
                grainClassNamePrefix,
                Utils.EnumerableToString(matches, d => d.GrainClass, ",", false)));
        }
    }
}
