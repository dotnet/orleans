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
        /// <summary>Invokes a fire and forget method on a remote object.</summary>
        /// <param name="reference">The reference to the addressable target.</param>
        /// <param name="methodId">The method to invoke.</param>
        /// <param name="arguments">The method payload.</param>
        /// <param name="options">Invocation options.</param>
        void InvokeOneWayMethod(GrainReference reference, int methodId, object[] arguments, InvokeMethodOptions options);

        /// <summary>Invokes a method on a remote object.</summary>
        /// <typeparam name="T">The result type</typeparam>
        /// <param name="reference">The reference to the addressable target.</param>
        /// <param name="methodId">The method to invoke.</param>
        /// <param name="arguments">The method payload.</param>
        /// <param name="options">Invocation options.</param>
        /// <returns>Returns the response from the remote object.</returns>
        Task<T> InvokeMethodAsync<T>(GrainReference reference, int methodId, object[] arguments, InvokeMethodOptions options);

        ValueTask<T> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options);

        /// <summary>
        /// Converts the provided <paramref name="grain"/> to the provided <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="interfaceType">The resulting interface type.</param>
        /// <returns>A reference to <paramref name="grain"/> which implements <paramref name="interfaceType"/>.</returns>
        object Cast(IAddressable grain, Type interfaceType);

        void SendRequest(GrainReference reference, IResponseCompletionSource callback, IInvokable body, InvokeMethodOptions options);
    }
}