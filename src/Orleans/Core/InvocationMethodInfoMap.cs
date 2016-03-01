namespace Orleans
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    using Orleans.CodeGeneration;

    internal class InvocationMethodInfoMap
    {
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<int, IReadOnlyDictionary<int, MethodInfo>>> implementations =
            new ConcurrentDictionary<Type, ConcurrentDictionary<int, IReadOnlyDictionary<int, MethodInfo>>>();

        /// <summary>
        /// Returns the <see cref="MethodInfo"/> for the specified implementation and invocation request.
        /// </summary>
        /// <param name="implementationType">The type of the implementation.</param>
        /// <param name="request">The invocation request.</param>
        /// <returns>The <see cref="MethodInfo"/> for the specified implementation and invocation request.</returns>
        public MethodInfo GetMethodInfo(Type implementationType, InvokeMethodRequest request)
        {
            var implementation = this.implementations.GetOrAdd(
                implementationType,
                _ => new ConcurrentDictionary<int, IReadOnlyDictionary<int, MethodInfo>>());

            IReadOnlyDictionary<int, MethodInfo> interfaceMap;
            if (!implementation.TryGetValue(request.InterfaceId, out interfaceMap))
            {
                // Get the interface mapping for the current implementation.
                var interfaces = GrainInterfaceData.GetRemoteInterfaces(implementationType);
                Type interfaceType;
                if (!interfaces.TryGetValue(request.InterfaceId, out interfaceType))
                {
                    // The specified type does not implement the provided interface.
                    return null;
                }

                // Create a mapping between the interface and the implementation.
                interfaceMap = implementation.GetOrAdd(
                    request.InterfaceId,
                    _ => MapInterfaceToImplementation(interfaceType, implementationType));
            }

            // Attempt to retrieve the implementation's MethodInfo.
            MethodInfo result;
            interfaceMap.TryGetValue(request.MethodId, out result);
            return result;
        }

        /// <summary>
        /// Maps the provided <paramref name="interfaceType"/> to the provided <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="interfaceType">The interface type.</param>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>The mapped interface.</returns>
        private static IReadOnlyDictionary<int, MethodInfo> MapInterfaceToImplementation(
            Type interfaceType,
            Type implementationType)
        {
            var interfaceMapping = implementationType.GetInterfaceMap(interfaceType);

            // Map the interface methods to implementation methods.
            var interfaceMethods = GrainInterfaceData.GetMethods(interfaceType);
            return interfaceMethods.ToDictionary(
                GrainInterfaceData.ComputeMethodId,
                interfaceMethod => GetImplementingMethod(interfaceMethod, interfaceMapping));
        }

        /// <summary>
        /// Returns the <see cref="MethodInfo"/> of implementation of <paramref name="interfaceMethod"/>.
        /// </summary>
        /// <param name="interfaceMethod">The interface method.</param>
        /// <param name="implementation">The implementation.</param>
        /// <returns>The <see cref="MethodInfo"/> of implementation of <paramref name="interfaceMethod"/>.</returns>
        private static MethodInfo GetImplementingMethod(MethodInfo interfaceMethod, InterfaceMapping implementation)
        {
            for (var i = 0; i < implementation.InterfaceMethods.Length; i++)
            {
                if (implementation.InterfaceMethods[i] == interfaceMethod)
                {
                    return implementation.TargetMethods[i];
                }
            }

            return null;
        }
    }
}