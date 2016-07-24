using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// An <see cref="IGrainMethodInvoker"/> which redirects execution for grains which implement
    /// <see cref="IGrainInvokeInterceptor"/>.
    /// </summary>
    internal class InterceptedMethodInvoker : IGrainMethodInvoker
    {
        /// <summary>
        /// The underlying invoker.
        /// </summary>
        private readonly IGrainMethodInvoker invoker;

        /// <summary>
        /// The grain's methods.
        /// </summary>
        private readonly Dictionary<int, MethodInfo> methodInfos;

        /// <summary>
        /// Constructs a new instance of the <see cref="InterceptedMethodInvoker"/> class.
        /// </summary>
        /// <param name="invoker">The underlying method invoker.</param>
        /// <param name="methodInfos">
        /// The mapping between method id and <see cref="MethodInfo"/> for each of the grain's methods.
        /// </param>
        public InterceptedMethodInvoker(IGrainMethodInvoker invoker, Dictionary<int, MethodInfo> methodInfos)
        {
            this.invoker = invoker;
            this.methodInfos = methodInfos;
        }

        /// <summary>
        /// Gets the interface id from the underlying invoker.
        /// </summary>
        public int InterfaceId => this.invoker.InterfaceId;

        /// <summary>
        /// Invoke a grain method.
        /// Invoker classes in generated code implement this method to provide a method call jump-table to map invoke
        /// data to a strongly typed call to the correct method on the correct interface.
        /// </summary>
        /// <param name="grain">Reference to the grain to be invoked.</param>
        /// <param name="request">The request being invoked.</param>
        /// <returns>Value promise for the result of the method invoke.</returns>
        public Task<object> Invoke(IAddressable grain, InvokeMethodRequest request)
        {
            // If the grain implements IGrainInvokeInterceptor then call its implementation, passing
            // the underlying invoker so that the grain can easily dispatch invocation to the correct method.
            var interceptor = grain as IGrainInvokeInterceptor;
            if (interceptor != null)
            {
                var methodInfo = this.GetMethodInfo(request.MethodId);
                return interceptor.Invoke(methodInfo, request, this.invoker);
            }

            // Otherwise, call the underlying invoker directly.
            return this.invoker.Invoke(grain, request);
        }

        /// <summary>
        /// Returns the method info for the provided <paramref name="methodId"/>, or <see langword="null"/> if not
        /// found.
        /// </summary>
        /// <param name="methodId">The method id.</param>
        /// <returns>
        /// The method info for the provided <paramref name="methodId"/>, or <see langword="null"/> if not found.
        /// </returns>
        public MethodInfo GetMethodInfo(int methodId)
        {
            // Attempt to retrieve the implementation's MethodInfo.
            MethodInfo result;
            this.methodInfos.TryGetValue(methodId, out result);
            return result;
        }
    }
}