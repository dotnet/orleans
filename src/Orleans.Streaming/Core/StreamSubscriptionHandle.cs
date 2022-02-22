using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Handle representing this subscription.
    /// Consumer may serialize and store the handle in order to unsubscribe later, for example
    /// in another activation on this grain.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public abstract class StreamSubscriptionHandle<T> : IEquatable<StreamSubscriptionHandle<T>>
    {
        /// <summary>
        /// Gets the stream identifier.
        /// </summary>
        /// <value>The stream identifier.</value>
        public abstract StreamId StreamId { get; }

        /// <summary>
        /// Gets the name of the provider.
        /// </summary>
        /// <value>The name of the provider.</value>
        public abstract string ProviderName { get; }

        /// <summary>
        /// Gets the unique identifier for this StreamSubscriptionHandle
        /// </summary>
        public abstract Guid HandleId { get; }

        /// <summary>
        /// Unsubscribe a stream consumer from this observable.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the operation.
        /// </returns>
        public abstract Task UnsubscribeAsync();

        /// <summary>
        /// Resumed consumption from a subscription to a stream.
        /// </summary>
        /// <param name="observer">The observer object.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>
        /// The new stream subscription handle.
        /// </returns>
        public abstract Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null);

        /// <summary>
        /// Resume batch consumption from a subscription to a stream.
        /// </summary>
        /// <param name="observer">The batcj bserver object.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>
        /// The new stream subscription handle.
        /// </returns>
        public abstract Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncBatchObserver<T> observer, StreamSequenceToken token = null);

        /// <inheritdoc/>
        public abstract bool Equals(StreamSubscriptionHandle<T> other);
    }
}
