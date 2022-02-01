using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;
using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Runtime logic for <see cref="GrainReference"/>s to be usable.
    /// This service is not meant to be used directly by user code.
    /// </summary>
    public interface IGrainReferenceRuntime
    {
        /// <summary>
        /// Invokes the specified method on the provided grain interface.
        /// </summary>
        /// <typeparam name="T">The underlying return type of the method.</typeparam>
        /// <param name="reference">The grain reference.</param>
        /// <param name="request">The method description.</param>
        /// <param name="options">The invocation options.</param>
        /// <returns>The result of invocation.</returns>
        ValueTask<T> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options);

        /// <summary>
        /// Invokes the specified method on the provided grain interface.
        /// </summary>
        /// <param name="reference">The grain reference.</param>
        /// <param name="request">The method description.</param>
        /// <param name="options">The invocation options.</param>
        /// <returns>A <see cref="ValueTask"/> representing the operation</returns>
        ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options);

        /// <summary>
        /// Converts the provided <paramref name="grain"/> to the provided <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="interfaceType">The resulting interface type.</param>
        /// <returns>A reference to <paramref name="grain"/> which implements <paramref name="interfaceType"/>.</returns>
        object Cast(IAddressable grain, Type interfaceType);
    }
}