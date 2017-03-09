using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// An implementation of this interface is generated for every grain extension as part of the client-side code generation.
    /// </summary>
    public interface IGrainExtensionMethodInvoker : IGrainMethodInvoker
    {
        /// <summary>
        /// Invoke a grain extension method.
        /// </summary>
        /// <param name="extension">Reference to the extension to be invoked.</param>
        /// <param name="request">The request being invoked.</param>
        /// <returns>Value promise for the result of the method invoke.</returns>
        Task<object> Invoke(IGrainExtension extension, InvokeMethodRequest request);
    }
}