using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Maintains a map between grain classes and corresponding <see cref="InterceptedMethodInvoker"/> instances.
    /// </summary>
    internal class InterceptedMethodInvokerCache
    {
        /// <summary>
        /// The map from implementation types to interface ids to invoker.
        /// </summary>
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<int, InterceptedMethodInvoker>> invokers =
            new ConcurrentDictionary<Type, ConcurrentDictionary<int, InterceptedMethodInvoker>>();

        /// <summary>
        /// Returns a grain method invoker which calls the grain's implementation of <see cref="IGrainInvokeInterceptor"/>
        /// if it exists, otherwise calling the provided <paramref name="invoker"/> directly.
        /// </summary>
        /// <param name="implementationType">The grain type.</param>
        /// <param name="interfaceId">The interface id.</param>
        /// <param name="invoker">
        /// The underlying invoker, which will be passed to the grain's <see cref="IGrainInvokeInterceptor.Invoke"/>
        /// method.
        /// </param>
        /// <returns>
        /// A grain method invoker which calls the grain's implementation of <see cref="IGrainInvokeInterceptor"/>.
        /// </returns>
        public InterceptedMethodInvoker GetOrCreate(Type implementationType, int interfaceId, IGrainMethodInvoker invoker)
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
                _ => CreateInterceptedMethodInvoker(implementationType, interfaceId, invoker));
        }

        /// <summary>
        /// Returns a new <see cref="InterceptedMethodInvoker"/> for the provided values.
        /// </summary>
        /// <param name="implementationType">The grain type.</param>
        /// <param name="interfaceId">The interface id.</param>
        /// <param name="invoker">The invoker.</param>
        /// <returns>A new <see cref="InterceptedMethodInvoker"/> for the provided values.</returns>
        private static InterceptedMethodInvoker CreateInterceptedMethodInvoker(
            Type implementationType,
            int interfaceId,
            IGrainMethodInvoker invoker)
        {
            // If a grain extension is being invoked, the implementation map must match the methods on the extension
            // and not the grain implementation.
            var extensionMap = invoker as IGrainExtensionMap;
            if (extensionMap != null)
            {
                IGrainExtension extension;
                if (extensionMap.TryGetExtension(interfaceId, out extension))
                {
                    implementationType = extension.GetType();
                }
            }

            var implementationMap = GetInterfaceToImplementationMap(interfaceId, implementationType);
            return new InterceptedMethodInvoker(invoker, implementationMap);
        }

        /// <summary>
        /// Maps the provided <paramref name="interfaceId"/> to the provided <paramref name="implementationType"/>.
        /// </summary>
        /// <param name="interfaceId">The interface id.</param>
        /// <param name="implementationType">The implementation type.</param>
        /// <returns>The mapped interface.</returns>
        private static Dictionary<int, MethodInfo> GetInterfaceToImplementationMap(
            int interfaceId,
            Type implementationType)
        {
            // Get the interface mapping for the current implementation.
            var interfaceTypes = GrainInterfaceUtils.GetRemoteInterfaces(implementationType);
            var interfaceType = interfaceTypes[interfaceId];
            var interfaceMapping = implementationType.GetTypeInfo().GetRuntimeInterfaceMap(interfaceType);

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