using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;

namespace Orleans.Providers
{
    /// <summary>
    /// Handles the invocation of the provided <paramref name="request"/>.
    /// </summary>
    /// <param name="targetMethod">The method on <paramref name="target"/> being invoked.</param>
    /// <param name="request">The request.</param>
    /// <param name="target">The invocation target.</param>
    /// <param name="invoker">
    /// The invoker which is used to dispatch the provided <paramref name="request"/> to the provided
    /// <paramref name="target"/>.
    /// </param>
    /// <returns>The result of invocation, which will be returned to the client.</returns>
    public delegate Task<object> InvokeInterceptor(
        MethodInfo targetMethod, InvokeMethodRequest request, IGrain target, IGrainMethodInvoker invoker);
}