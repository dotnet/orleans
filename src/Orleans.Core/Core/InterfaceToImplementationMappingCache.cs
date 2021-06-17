using System;
using System.Collections.Generic;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Utilities;

namespace Orleans
{
    /// <summary>
    /// Maintains a map between grain classes and corresponding interface-implementation mappings.
    /// </summary>
    internal class InterfaceToImplementationMappingCache
    {
        public readonly struct Entry
        {
            public Entry(MethodInfo implementationMethod, MethodInfo interfaceMethod)
            {
                ImplementationMethod = implementationMethod;
                InterfaceMethod = interfaceMethod;
            }

            public MethodInfo ImplementationMethod { get; }
            public MethodInfo InterfaceMethod { get; }
        }

        /// <summary>
        /// The map from implementation types to interface types to map of method to method infos.
        /// </summary>
        private readonly CachedReadConcurrentDictionary<Type, Dictionary<Type, Dictionary<MethodInfo, Entry>>> mappings =
            new CachedReadConcurrentDictionary<Type, Dictionary<Type, Dictionary<MethodInfo, Entry>>>();

        /// <summary>
        /// Returns a mapping from method id to method info for the provided implementation and interface types.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <param name="interfaceType">The interface type.</param>
        /// <returns>
        /// A mapping from method id to method info.
        /// </returns>
        public Dictionary<MethodInfo, Entry> GetOrCreate(Type implementationType, Type interfaceType)
        {
            // Get or create the mapping between interfaceId and invoker for the provided type.
            if (!this.mappings.TryGetValue(implementationType, out var invokerMap))
            {
                // Generate an the invoker mapping using the provided invoker.
                this.mappings[implementationType] = invokerMap = CreateInterfaceToImplementationMap(implementationType);
            }

            // Attempt to get the invoker for the provided interfaceId.
            if (!invokerMap.TryGetValue(interfaceType, out var interfaceToImplementationMap))
            {
                throw new InvalidOperationException($"Type {implementationType} does not implement interface {interfaceType}");
            }

            return interfaceToImplementationMap;
        }

        /// <summary>
        /// Maps the interfaces of the provided <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>The mapped interface.</returns>
        private static Dictionary<Type, Dictionary<MethodInfo, Entry>> CreateInterfaceToImplementationMap(Type implementationType)
        {
            var interfaces = implementationType.GetInterfaces();

            // Create an invoker for every interface on the provided type.
            var result = new Dictionary<Type, Dictionary<MethodInfo, Entry>>(interfaces.Length);
            var implementationTypeInfo = implementationType.GetTypeInfo();
            foreach (var iface in interfaces)
            {
                var methods = GrainInterfaceUtils.GetMethods(iface);

                // Map every method on this interface from the definition interface onto the implementation class.
                var methodMap = new Dictionary<MethodInfo, Entry>(methods.Length);

                var mapping = default(InterfaceMapping);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];

                    // If this method is not from the expected interface (eg, because it's from a parent interface), then
                    // get the mapping for the interface which it does belong to.
                    if (mapping.InterfaceType != method.DeclaringType)
                    {
                        mapping = implementationTypeInfo.GetRuntimeInterfaceMap(method.DeclaringType);
                    }

                    // Find the index of the interface method and then get the implementation method at that position.
                    for (var k = 0; k < mapping.InterfaceMethods.Length; k++)
                    {
                        if (mapping.InterfaceMethods[k] != method) continue;
                        methodMap[method] = new Entry(mapping.TargetMethods[k], method);

                        break;
                    }
                }

                // Add the resulting map of methodId -> method to the interface map.
                result[iface] = methodMap;
            }

            return result;
        }
    }
}