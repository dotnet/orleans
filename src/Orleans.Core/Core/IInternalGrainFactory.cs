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
        /// <param name="grainType">The type of the target.</param>
        /// <param name="destination">The destination silo.</param>
        /// <returns>A reference to the specified system target.</returns>
        TGrainInterface GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination)
            where TGrainInterface : ISystemTarget;

        /// <summary>
        /// Gets a reference to the specified system target.
        /// </summary>
        /// <typeparam name="TGrainInterface">The system target interface.</typeparam>
        /// <param name="grainId">The id of the target.</param>
        /// <returns>A reference to the specified system target.</returns>
        TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId) where TGrainInterface : ISystemTarget;
    }
}