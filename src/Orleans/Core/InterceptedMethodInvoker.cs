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
        private readonly IReadOnlyDictionary<int, MethodInfo> methodInfos;

        public InterceptedMethodInvoker(IGrainMethodInvoker invoker, IReadOnlyDictionary<int, MethodInfo> methodInfos)
        {
            this.invoker = invoker;
            this.methodInfos = methodInfos;
        }

        public int InterfaceId { get { return invoker.InterfaceId; } }

        public Task<object> Invoke(IAddressable grain, InvokeMethodRequest request)
        {
            var interceptor = grain as IGrainInvokeInterceptor;
            if (interceptor != null)
            {
                var methodInfo = this.GetMethodInfo(request.MethodId);
                return interceptor.Invoke(methodInfo, request, this.invoker);
            }

            return this.invoker.Invoke(grain, request);
        }

        public MethodInfo GetMethodInfo(int methodId)
        {
            // Attempt to retrieve the implementation's MethodInfo.
            MethodInfo result;
            this.methodInfos.TryGetValue(methodId, out result);
            return result;
        }
    }
}