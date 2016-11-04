using System;

using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// The internal grain factory interface.
    /// </summary>
    internal interface IInternalGrainFactory : IGrainFactory
    {
        /// <summary>
        /// Creates a reference to the provided object.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">The interface which interface.</typeparam>
        /// <param name="obj">The object.</param>
        /// <returns>A reference to the provided object.</returns>
        TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable;

        /// <summary>
        /// Gets a reference to the specified system target.
        /// </summary>
        /// <typeparam name="TGrainInterface">The system target interface.</typeparam>
        /// <param name="grainId">The id of the target.</param>
        /// <param name="destination">The destination silo.</param>
        /// <returns>A reference to the specified system target.</returns>
        TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
            where TGrainInterface : ISystemTarget;

        /// <summary>
        /// Casts the provided <paramref name="grain"/> to the specified interface
        /// </summary>
        /// <typeparam name="TGrainInterface">The target grain interface type.</typeparam>
        /// <param name="grain">The grain reference being cast.</param>
        /// <returns>
        /// A reference to <paramref name="grain"/> which implements <typeparamref name="TGrainInterface"/>.
        /// </returns>
        TGrainInterface Cast<TGrainInterface>(IAddressable grain);

        /// <summary>
        /// Casts the provided <paramref name="grain"/> to the specified interface
        /// </summary>
        /// <param name="grain">The grain reference being cast.</param>
        /// <param name="interfaceType">The target grain interface type.</param>
        /// <returns>
        /// A reference to <paramref name="grain"/> which implements <paramref name="interfaceType"/>.
        /// </returns>
        object Cast(IAddressable grain, Type interfaceType);
    }
}