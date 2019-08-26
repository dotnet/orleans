using Orleans.CodeGeneration;
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
        /// <param name="silo">The target silo.</param>
        void InvokeOneWayMethod(GrainReference reference, int methodId, object[] arguments, InvokeMethodOptions options, SiloAddress silo);

        /// <summary>Invokes a method on a remote object.</summary>
        /// <typeparam name="T">The result type</typeparam>
        /// <param name="reference">The reference to the addressable target.</param>
        /// <param name="methodId">The method to invoke.</param>
        /// <param name="arguments">The method payload.</param>
        /// <param name="options">Invocation options.</param>
        /// <param name="silo">The target silo.</param>
        /// <returns>Returns the response from the remote object.</returns>
        Task<T> InvokeMethodAsync<T>(GrainReference reference, int methodId, object[] arguments, InvokeMethodOptions options, SiloAddress silo);

        /// <summary>Converts the provided <paramref name="grain"/> to the specified interface.</summary>
        /// <typeparam name="TGrainInterface">The target grain interface type.</typeparam>
        /// <param name="grain">The grain reference being cast.</param>
        /// <returns>A reference to <paramref name="grain"/> which implements <typeparamref name="TGrainInterface"/>.</returns>
        TGrainInterface Convert<TGrainInterface>(IAddressable grain);

        /// <summary>
        /// Converts the provided <paramref name="grain"/> to the provided <paramref name="interfaceType"/>.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="interfaceType">The resulting interface type.</param>
        /// <returns>A reference to <paramref name="grain"/> which implements <paramref name="interfaceType"/>.</returns>
        object Convert(IAddressable grain, Type interfaceType);
    }
}