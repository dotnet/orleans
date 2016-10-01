namespace Orleans
{
    using System.Reflection;
    using System.Threading.Tasks;
    using Orleans.CodeGeneration;

    public interface IGrainInvokeInterceptor
    {
        Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker);
    }
}