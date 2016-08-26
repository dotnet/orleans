using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams.AdHoc
{
    /// <summary>
    /// The remote interface for grains which are observable.
    /// </summary>
    internal interface IObservableGrainExtension : IGrainExtension
    {
        [AlwaysInterleave]
        Task Subscribe(Guid streamId, InvokeMethodRequest request, IUntypedGrainObserver observer, StreamSequenceToken token);
        
        [AlwaysInterleave]
        Task Unsubscribe(Guid streamId);
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
    /// The remote interface for <see cref="IObserverGrainExtension"/>.
    /// </summary>
    internal interface IObserverGrainExtensionRemote : IGrainExtension, IUntypedGrainObserver
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

    /// <summary>
    /// The interface for the extension which allows grains to subscribe to observables.
    /// </summary>
    internal interface IObserverGrainExtension : IObserverGrainExtensionLocal, IObserverGrainExtensionRemote
    {
    }
}