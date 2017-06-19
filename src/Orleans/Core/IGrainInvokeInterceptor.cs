using System;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;

namespace Orleans
{
    [Obsolete("Use + " + nameof(IGrainCallFilter) + "instead. This interface may be removed in a future release.")]
    public interface IGrainInvokeInterceptor
    {
        Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker);
    }
}