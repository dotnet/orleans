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
            var interfaceTypes = GrainInterfaceUtils.GetRemoteInterfaces(implementationType);
            
            // Get all interface mappings of all interfaces.
            var interfaceMapping = implementationType
                .GetInterfaces()
                .Select(i => implementationType.GetTypeInfo().GetRuntimeInterfaceMap(i))
                .SelectMany(map => map.InterfaceMethods
                    .Zip(map.TargetMethods, (interfaceMethod, targetMethod) => new { interfaceMethod, targetMethod }))
                .ToArray();

            // Map the grain interface methods to the implementation methods.
            return GrainInterfaceUtils.GetMethods(interfaceTypes[interfaceId])
                .ToDictionary(GrainInterfaceUtils.ComputeMethodId,
                    m => interfaceMapping.SingleOrDefault(pair => pair.interfaceMethod == m)?.targetMethod);
        }
    }
}