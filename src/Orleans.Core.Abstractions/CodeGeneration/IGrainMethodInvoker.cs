using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.CodeGeneration
{
    /// <summary>
    /// An implementation of this interface is generated for every grain interface as part of the client-side code generation.
    /// </summary>
    public interface IGrainMethodInvoker
    {
        /// <summary>
        /// Invoke a grain method.
        /// Invoker classes in generated code implement this method to provide a method call jump-table to map invoke data to a strongly typed call to the correct method on the correct interface.
        /// </summary>
        /// <param name="context">Reference to the grain to be invoked.</param>
        /// <param name="request">The request being invoked.</param>
        /// <returns>Value promise for the result of the method invoke.</returns>
        Task<object> Invoke(IGrainContext context, InvokeMethodRequest request);

        /// <summary>
        /// The interface type which this invoker supports.
        /// </summary>
        Type InterfaceType { get; }

        /// <summary>
        /// Returns the target object for invocation.
        /// </summary>
        /// <param name="context">The grain context.</param>
        object GetTarget(IGrainContext context);
    }

    /// <summary>
    /// An implementation of this interface is generated for every grain extension as part of the client-side code generation.
    /// </summary>
    public interface IGrainExtensionMethodInvoker : IGrainMethodInvoker
    {
    }
}