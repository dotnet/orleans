using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// This interface represents an object that serves as a distributed rendezvous between producers and consumers.
    /// It is similar to a Reactive Framework <code>Subject</code> and implements
    /// <code>IObserver</code> nor <code>IObservable</code> interfaces.
    /// </summary>
    /// <typeparam name="T">The type of object that flows through the stream.</typeparam>
    public interface IAsyncStream<T> :
        IEquatable<IAsyncStream<T>>, IComparable<IAsyncStream<T>>, // comparison
        IAsyncObservable<T>, IAsyncBatchObservable<T>, // observables
        IAsyncBatchProducer<T> // observers
    {
        /// <summary>
        /// Determines whether this is a rewindable stream - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream, false otherwise.</returns>
        bool IsRewindable { get; }

        /// <summary> Stream Provider Name. </summary>
        string ProviderName { get; }

        StreamId StreamId { get; }

        /// <summary>
        /// Retrieves a list of all active subscriptions created by the caller for this stream.
        /// </summary>
        /// <returns></returns>
        Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptionHandles();
    }
}
