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
        /// <summary>
        /// The map from implementation types to interface ids to map of method ids to method infos.
        /// </summary>
        private readonly CachedReadConcurrentDictionary<Type, Dictionary<int, Dictionary<int, MethodInfo>>> mappings =
            new CachedReadConcurrentDictionary<Type, Dictionary<int, Dictionary<int, MethodInfo>>>();

        /// <summary>
        /// Returns a mapping from method id to method info for the provided implementation and interface id.
        /// </summary>
        /// <param name="implementationType">The grain type.</param>
        /// <param name="interfaceId">The interface id.</param>
        /// <returns>
        /// A mapping from method id to method info.
        /// </returns>
        public Dictionary<int, MethodInfo> GetOrCreate(Type implementationType, int interfaceId)
        {
            // Get or create the mapping between interfaceId and invoker for the provided type.
            Dictionary<int, Dictionary<int, MethodInfo>> invokerMap;
            if (!this.mappings.TryGetValue(implementationType, out invokerMap))
            {
                // Generate an the invoker mapping using the provided invoker.
                this.mappings[implementationType] = invokerMap = CreateInterfaceToImplementationMap(implementationType);
            }

            // Attempt to get the invoker for the provided interfaceId.
            Dictionary<int, MethodInfo> interfaceToImplementationMap;
            if (!invokerMap.TryGetValue(interfaceId, out interfaceToImplementationMap))
            {
                throw new InvalidOperationException(
                    $"Type {implementationType} does not implement interface with id {interfaceId} ({interfaceId:X}).");
            }

            return interfaceToImplementationMap;
        }

        /// <summary>
        /// Maps the interfaces of the provided <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>The mapped interface.</returns>
        private static Dictionary<int, Dictionary<int, MethodInfo>> CreateInterfaceToImplementationMap(Type implementationType)
        {
            if (implementationType.IsConstructedGenericType) return CreateMapForConstructedGeneric(implementationType);
            return CreateMapForNonGeneric(implementationType);
        }

        /// <summary>
        /// Creates and returns a map from interface id to map of method id to method info for the provided non-generic type.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>A map from interface id to map of method id to method info for the provided type.</returns>
        private static Dictionary<int, Dictionary<int, MethodInfo>> CreateMapForNonGeneric(Type implementationType)
        {
            if (implementationType.IsConstructedGenericType)
            {
                throw new InvalidOperationException(
                    $"Type {implementationType} passed to {nameof(CreateMapForNonGeneric)} is a constructed generic type.");
            }

            var implementationTypeInfo = implementationType.GetTypeInfo();
            var interfaces = implementationType.GetInterfaces();

            // Create an invoker for every interface on the provided type.
            var result = new Dictionary<int, Dictionary<int, MethodInfo>>(interfaces.Length);
            foreach (var iface in interfaces)
            {
                var methods = GrainInterfaceUtils.GetMethods(iface);

                // Map every method on this interface from the definition interface onto the implementation class.
                var methodMap = new Dictionary<int, MethodInfo>(methods.Length);
                var mapping = default(InterfaceMapping);
                foreach (var method in methods)
                {
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
                        methodMap[GrainInterfaceUtils.ComputeMethodId(method)] = mapping.TargetMethods[k];
                        break;
                    }
                }

                // Add the resulting map of methodId -> method to the interface map.
                var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(iface);
                result[interfaceId] = methodMap;
            }

            return result;
        }

        /// <summary>
        /// Creates and returns a map from interface id to map of method id to method info for the provided constructed generic type.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>A map from interface id to map of method id to method info for the provided type.</returns>
        private static Dictionary<int, Dictionary<int, MethodInfo>> CreateMapForConstructedGeneric(Type implementationType)
        {
            // It is important to note that the interfaceId and methodId are computed based upon the non-concrete
            // version of the implementation type. During code generation, the concrete type would not be available
            // and therefore the generic type definition is used.
            if (!implementationType.IsConstructedGenericType)
            {
                throw new InvalidOperationException(
                    $"Type {implementationType} passed to {nameof(CreateMapForConstructedGeneric)} is not a constructed generic type");
            }

            var genericClass = implementationType.GetGenericTypeDefinition();
            var genericClassTypeInfo = genericClass.GetTypeInfo();
            var implementationTypeInfo = implementationType.GetTypeInfo();

            var genericInterfaces = genericClass.GetInterfaces();
            var concreteInterfaces = implementationType.GetInterfaces();

            // Create an invoker for every interface on the provided type.
            var result = new Dictionary<int, Dictionary<int, MethodInfo>>(genericInterfaces.Length);
            for (var i = 0; i < genericInterfaces.Length; i++)
            {
                // Because these methods are identical except for type parameters, their methods should also be identical except
                // for type parameters, including identical ordering. That is the assumption.
                var genericMethods = GrainInterfaceUtils.GetMethods(genericInterfaces[i]);
                var concreteInterfaceMethods = GrainInterfaceUtils.GetMethods(concreteInterfaces[i]);

                // Map every method on this interface from the definition interface onto the implementation class.
                var methodMap = new Dictionary<int, MethodInfo>(genericMethods.Length);
                var genericMap = default(InterfaceMapping);
                var concreteMap = default(InterfaceMapping);
                for (var j = 0; j < genericMethods.Length; j++)
                {
                    // If this method is not from the expected interface (eg, because it's from a parent interface), then
                    // get the mapping for the interface which it does belong to.
                    var genericInterfaceMethod = genericMethods[j];
                    if (genericMap.InterfaceType != genericInterfaceMethod.DeclaringType)
                    {
                        genericMap = genericClassTypeInfo.GetRuntimeInterfaceMap(genericInterfaceMethod.DeclaringType);
                        concreteMap = implementationTypeInfo.GetRuntimeInterfaceMap(concreteInterfaceMethods[j].DeclaringType);
                    }

                    // Determine the position in the definition's map which the target method belongs to and take the implementation
                    // from the same position on the implementation's map.
                    for (var k = 0; k < genericMap.InterfaceMethods.Length; k++)
                    {
                        if (genericMap.InterfaceMethods[k] != genericInterfaceMethod) continue;
                        methodMap[GrainInterfaceUtils.ComputeMethodId(genericInterfaceMethod)] = concreteMap.TargetMethods[k];
                        break;
                    }
                }

                // Add the resulting map of methodId -> method to the interface map.
                var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(genericInterfaces[i]);
                result[interfaceId] = methodMap;
            }

            return result;
        }
    }
}