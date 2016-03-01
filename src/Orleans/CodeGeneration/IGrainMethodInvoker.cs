using System.Threading.Tasks;

using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// An implementation of this interface is generated for every grain interface as part of the client-side code generation.
    /// </summary>
    public interface IGrainMethodInvoker
    {
        /// <summary> The interface id that this invoker supports. </summary>
        int InterfaceId { get; }

        /// <summary>
        /// Invoke a grain method.
        /// Invoker classes in generated code implement this method to provide a method call jump-table to map invoke data to a strongly typed call to the correct method on the correct interface.
        /// </summary>
        /// <param name="grain">Reference to the grain to be invoked.</param>
        /// <param name="request">The request being invoked.</param>
        /// <returns>Value promise for the result of the method invoke.</returns>
        Task<object> Invoke(IAddressable grain, InvokeMethodRequest request);
    }

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