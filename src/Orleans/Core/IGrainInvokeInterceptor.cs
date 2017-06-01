using System;
using Orleans.Providers;

namespace Orleans
{
    using System.Reflection;
    using System.Threading.Tasks;
    using Orleans.CodeGeneration;

    [Obsolete("Use IMethodInvocationInterceptor instead. This interface may be removed in a future release.")]
    public interface IGrainInvokeInterceptor
    {
        Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker);
    }

    public interface IGrainCallFilter
    {
        Task Invoke(IGrainCallContext ctx);
    }
}