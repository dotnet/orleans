using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.CodeGeneration;

namespace Orleans
{
    internal class InvocationMethodInfoMap
    {
        /// <summary>
        /// The map from implementation types to interface ids to invoker.
        /// </summary>
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<int, InterceptedMethodInvoker>> invokers =
            new ConcurrentDictionary<Type, ConcurrentDictionary<int, InterceptedMethodInvoker>>();

        public InterceptedMethodInvoker GetInterceptedMethodInvoker(Type implementationType, int interfaceId, IGrainMethodInvoker invoker)
        {
            var implementation = invokers.GetOrAdd(
                implementationType,
                _ => new ConcurrentDictionary<int, InterceptedMethodInvoker>());

            InterceptedMethodInvoker interceptedMethodInvoker;
            if (implementation.TryGetValue(interfaceId, out interceptedMethodInvoker))
            {
                return interceptedMethodInvoker;
            }

            // Create a mapping between the interface and the implementation.
            return implementation.GetOrAdd(
                interfaceId,
                _ =>
                    new InterceptedMethodInvoker(invoker,
                        GetInterfaceToImplementationMap(interfaceId, implementationType)));
        }

        /// <summary>
        /// Maps the provided <paramref name="interfaceId"/> to the provided <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="interfaceType">The interface type.</param>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>The mapped interface.</returns>
        private static IReadOnlyDictionary<int, MethodInfo> GetInterfaceToImplementationMap(
            int interfaceId,
            Type implementationType)
        {
            // Get the interface mapping for the current implementation.
            var interfaceTypes = GrainInterfaceUtils.GetRemoteInterfaces(implementationType);
            var interfaceType = interfaceTypes[interfaceId];
            var interfaceMapping = implementationType.GetInterfaceMap(interfaceType);

            // Map the interface methods to implementation methods.
            var interfaceMethods = GrainInterfaceUtils.GetMethods(interfaceType);
            return interfaceMethods.ToDictionary(
                GrainInterfaceUtils.ComputeMethodId,
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