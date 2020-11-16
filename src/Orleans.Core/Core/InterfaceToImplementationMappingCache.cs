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
        /// The map from implementation types to interface types to map of method ids to method infos.
        /// </summary>
        private readonly CachedReadConcurrentDictionary<Type, Dictionary<Type, Dictionary<int, Entry>>> mappings =
            new CachedReadConcurrentDictionary<Type, Dictionary<Type, Dictionary<int, Entry>>>();

        /// <summary>
        /// Returns a mapping from method id to method info for the provided implementation and interface types.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <param name="interfaceType">The interface type.</param>
        /// <returns>
        /// A mapping from method id to method info.
        /// </returns>
        public Dictionary<int, Entry> GetOrCreate(Type implementationType, Type interfaceType)
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
        private static Dictionary<Type, Dictionary<int, Entry>> CreateInterfaceToImplementationMap(Type implementationType)
        {
            if (implementationType.IsConstructedGenericType) return CreateMapForConstructedGeneric(implementationType);
            return CreateMapForNonGeneric(implementationType);
        }

        /// <summary>
        /// Creates and returns a map from interface type to map of method id to method info for the provided non-generic type.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>A map from interface type to map of method id to method info for the provided type.</returns>
        private static Dictionary<Type, Dictionary<int, Entry>> CreateMapForNonGeneric(Type implementationType)
        {
            if (implementationType.IsConstructedGenericType)
            {
                throw new InvalidOperationException(
                    $"Type {implementationType} passed to {nameof(CreateMapForNonGeneric)} is a constructed generic type.");
            }

            var concreteInterfaces = implementationType.GetInterfaces();
            var interfaces = new List<(Type Concrete, Type Generic)>(concreteInterfaces.Length);
            foreach (var iface in concreteInterfaces)
            {
                if (iface.IsConstructedGenericType)
                {
                    interfaces.Add((iface, iface.GetGenericTypeDefinition()));
                }
                else
                {
                    interfaces.Add((iface, null));
                }
            }

            // Create an invoker for every interface on the provided type.
            var result = new Dictionary<Type, Dictionary<int, Entry>>(interfaces.Count);
            var implementationTypeInfo = implementationType.GetTypeInfo();
            foreach (var (iface, genericIface) in interfaces)
            {
                var methods = GrainInterfaceUtils.GetMethods(iface);
                var genericIfaceMethods = genericIface is object ? GrainInterfaceUtils.GetMethods(genericIface) : null;

                // Map every method on this interface from the definition interface onto the implementation class.
                var methodMap = new Dictionary<int, Entry>(methods.Length);
                var genericInterfaceMethodMap = genericIface is object ? new Dictionary<int, Entry>(genericIfaceMethods.Length) : null;

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
                        methodMap[GrainInterfaceUtils.ComputeMethodId(method)] = new Entry(mapping.TargetMethods[k], method);

                        if (genericIface is object)
                        {
                            var id = GrainInterfaceUtils.ComputeMethodId(genericIfaceMethods[i]);
                            genericInterfaceMethodMap[id] = new Entry(mapping.TargetMethods[k], genericIfaceMethods[i]);
                            methodMap[id] = new Entry(mapping.TargetMethods[k], method);
                        }

                        break;
                    }
                }

                // Add the resulting map of methodId -> method to the interface map.
                result[iface] = methodMap;

                if (genericIface is object)
                {
                    result[genericIface] = genericInterfaceMethodMap;
                }
            }

            return result;
        }

        /// <summary>
        /// Creates and returns a map from interface type to map of method id to method info for the provided constructed generic type.
        /// </summary>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>A map from interface type to map of method id to method info for the provided type.</returns>
        private static Dictionary<Type, Dictionary<int, Entry>> CreateMapForConstructedGeneric(Type implementationType)
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

            var genericInterfaces = genericClass.GetInterfaces();
            var concreteInterfaces = implementationType.GetInterfaces();

            // Create an invoker for every interface on the provided type.
            var result = new Dictionary<Type, Dictionary<int, Entry>>(genericInterfaces.Length);
            for (var i = 0; i < genericInterfaces.Length; i++)
            {
                // Because these methods are identical except for type parameters, their methods should also be identical except
                // for type parameters, including identical ordering. That is the assumption.
                var genericMethods = GrainInterfaceUtils.GetMethods(genericInterfaces[i]);
                var concreteInterfaceMethods = GrainInterfaceUtils.GetMethods(concreteInterfaces[i]);

                // Map every method on this interface from the definition interface onto the implementation class.
                var methodMap = new Dictionary<int, Entry>(genericMethods.Length);
                var genericMap = default(InterfaceMapping);
                var concreteMap = default(InterfaceMapping);
                for (var j = 0; j < genericMethods.Length; j++)
                {
                    // If this method is not from the expected interface (eg, because it's from a parent interface), then
                    // get the mapping for the interface which it does belong to.
                    var genericInterfaceMethod = genericMethods[j];
                    if (genericMap.InterfaceType != genericInterfaceMethod.DeclaringType)
                    {
                        genericMap = genericClass.GetTypeInfo().GetRuntimeInterfaceMap(genericInterfaceMethod.DeclaringType);
                        concreteMap = implementationType.GetTypeInfo().GetRuntimeInterfaceMap(concreteInterfaceMethods[j].DeclaringType);
                    }

                    // Determine the position in the definition's map which the target method belongs to and take the implementation
                    // from the same position on the implementation's map.
                    for (var k = 0; k < genericMap.InterfaceMethods.Length; k++)
                    {
                        if (genericMap.InterfaceMethods[k] != genericInterfaceMethod) continue;
                        methodMap[GrainInterfaceUtils.ComputeMethodId(genericInterfaceMethod)] = new Entry(concreteMap.TargetMethods[k], concreteMap.InterfaceMethods[k]);
                        break;
                    }
                }

                // Add the resulting map of methodId -> method to the interface map.
                result[concreteInterfaces[i]] = methodMap;
            }

            return result;
        }
    }
}