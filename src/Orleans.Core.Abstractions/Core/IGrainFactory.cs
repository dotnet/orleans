using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Functionality for creating references to grains.
    /// </summary>
    public interface IGrainFactory
    {
        /// <summary>
        /// Creates a reference to the provided <paramref name="obj"/>.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The object to create a reference to.</param>
        /// <returns>The reference to <paramref name="obj"/>.</returns>
        TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Deletes the provided object reference.
        /// </summary>
        /// <typeparam name="TGrainObserverInterface">
        /// The specific <see cref="IGrainObserver"/> type of <paramref name="obj"/>.
        /// </typeparam>
        /// <param name="obj">The reference being deleted.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver;

        /// <summary>
        /// Returns a reference to the specified grain which implements the specified grain interface type and has the specified grain key, without specifying the grain type directly.
        /// </summary>
        /// <remarks>
        /// This method infers the most appropriate <see cref="GrainId.Type"/> value based on the <paramref name="interfaceType"/> argument and optional <paramref name="grainClassNamePrefix"/> argument.
        /// </remarks>
        /// <param name="interfaceType">The interface type which the returned grain reference will implement.</param>
        /// <param name="grainKey">The <see cref="GrainId.Key"/> portion of the grain id.</param>
        /// <param name="grainClassNamePrefix">An optional grain class name prefix.</param>
        /// <returns>A grain reference which implements the provided interface.</returns>
        IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix);

        /// <summary>
        /// Returns a reference to the specified grain which implements the specified interface.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <typeparam name="TGrainInterface">
        /// The grain interface type which the returned grain reference must implement.
        /// </typeparam>
        /// <returns>
        /// A reference to the specified grain which implements the specified interface.
        /// </returns>
        TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable;

        /// <summary>
        /// Returns a reference for the provided grain id which implements the specified interface type.
        /// </summary>
        /// <param name="grainId">
        /// The grain id.
        /// </param>
        /// <param name="interfaceType">
        /// The interface type which the returned grain reference must implement.
        /// </param>
        /// <returns>
        /// A reference for the provided grain id which implements the specified interface type.
        /// </returns>
        IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType);
    }
}