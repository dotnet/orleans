using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams.AdHoc
{
    /// <summary>
    /// Represents an untyped observer.
    /// </summary>
    internal interface IUntypedGrainObserver : IAddressable
    {
        /// <summary>
        /// Called when a new value has been produced in the specified stream.
        /// </summary>
        /// <param name="streamId">The stream id.</param>
        /// <param name="value">The value.</param>
        /// <param name="token">The stream sequence token/</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task OnNextAsync(Guid streamId, object value, StreamSequenceToken token);

        /// <summary>
        /// Called when specified stream has terminated with an error.
        /// </summary>
        /// <param name="streamId">The stream id.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task OnErrorAsync(Guid streamId, Exception exception);

        /// <summary>
        /// Called when the specified stream has completed.
        /// </summary>
        /// <param name="streamId">The stream id.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task OnCompletedAsync(Guid streamId);
    }

    /// <summary>
    /// Methods for managing grain extensions for the current activation.
    /// </summary>
    internal interface IGrainExtensionManager
    {
        /// <summary>
        /// Gets the <typeparamref name="TExtension"/> extension handler for the current grain, returning <see langword="true"/> if successful
        /// or <see langword="false"/> otherwise.
        /// </summary>
        /// <typeparam name="TExtension">The extension interface type.</typeparam>
        /// <param name="result">The extension handler.</param>
        /// <returns><see langword="true"/> if successful or <see langword="false"/> otherwise.</returns>
        bool TryGetExtensionHandler<TExtension>(out TExtension result) where TExtension : IGrainExtension;

        /// <summary>
        /// Adds an extension handler to the current grain.
        /// </summary>
        /// <param name="handler">The extension handler.</param>
        /// <param name="extensionType">The extension interface type which the handler will be registered for.</param>
        /// <returns><see langword="true"/> if successful or <see langword="false"/> otherwise.</returns>
        bool TryAddExtension(IGrainExtension handler, Type extensionType);

        /// <summary>
        /// Removes an extension handler from the current grain.
        /// </summary>
        /// <param name="handler">The extension handler to remove.</param>
        void RemoveExtension(IGrainExtension handler);
    }

    /// <summary>
    /// The <see cref="IObserverGrainExtension"/> manager, which allows grains to subscribe to observables.
    /// </summary>
    internal interface IObserverGrainExtensionManager
    {
        /// <summary>
        /// Returns the <see cref="IObserverGrainExtension"/> for the current grain, installing it if required.
        /// </summary>
        /// <returns>The <see cref="IObserverGrainExtension"/> for the current grain.</returns>
        IObserverGrainExtension GetOrAddExtension();
    }

    /// <summary>
    /// The remote interface for grains which are observable.
    /// </summary>
    internal interface IObservableGrainExtension : IGrainExtension
    {
        [AlwaysInterleave]
        Task Subscribe(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver receiver, StreamSequenceToken token);
        
        [AlwaysInterleave]
        Task Unsubscribe(Guid streamId);
    }

    /// <summary>
    /// The remote interface for <see cref="IObserverGrainExtension"/>.
    /// </summary>
    internal interface IObserverGrainExtensionRemote : IGrainExtension, IUntypedGrainObserver
    {
    }

    /// <summary>
    /// The interface for the extension which allows grains to subscribe to observables.
    /// </summary>
    internal interface IObserverGrainExtension : IObserverGrainExtensionLocal, IObserverGrainExtensionRemote
    {
    }

    /// <summary>
    /// Defines the local interface for <see cref="IObserverGrainExtension"/>.
    /// </summary>
    internal interface IObserverGrainExtensionLocal
    {
        /// <summary>
        /// Registers an observer as the observer for the specified stream.
        /// </summary>
        /// <param name="streamId">The id of the stream being observed.</param>
        /// <param name="observer">The observer.</param>
        void Register(Guid streamId, IUntypedGrainObserver observer);
    }
}