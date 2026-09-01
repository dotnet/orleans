using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// This interface generalizes the IAsyncObserver interface to allow production and consumption of batches of items.
    /// <para>
    /// Note that this interface is implemented by item consumers and invoked (used) by item producers.
    /// This means that the consumer endpoint of a stream implements this interface.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type of object consumed by the observer.</typeparam>
    public interface IAsyncBatchObservable<T>
    {
        /// <summary>
        /// Subscribe a consumer to this batch observable.
        /// </summary>
        /// <param name="observer">The asynchronous batch observer to subscribe.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.
        /// </returns>
        Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> observer);

        /// <summary>
        /// Subscribe a consumer to this batch observable using the specified initial position.
        /// </summary>
        /// <param name="observer">The asynchronous batch observer to subscribe.</param>
        /// <param name="options">The subscription options.</param>
        /// <returns>A promise for a <see cref="StreamSubscriptionHandle{T}"/> that represents the subscription.</returns>
        /// <remarks>
        /// <see cref="StreamSubscriptionStartPosition.EarliestAvailable"/> is supported by rewindable persistent streams
        /// whose local queue cache supports start-position selection.
        /// </remarks>
        Task<StreamSubscriptionHandle<T>> SubscribeWithOptionsAsync(IAsyncBatchObserver<T> observer, StreamSubscriptionOptions options)
        {
            options.Validate();
            if (options.StartPosition == StreamSubscriptionStartPosition.Latest)
            {
                return SubscribeAsync(observer, null);
            }

            throw new NotSupportedException(
                $"{GetType().FullName} does not support {StreamSubscriptionStartPosition.EarliestAvailable} subscriptions.");
        }

        /// <summary>
        /// Subscribe a consumer to this batch observable.
        /// </summary>
        /// <param name="observer">The asynchronous batch observer to subscribe.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.
        /// </returns>
        Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> observer, StreamSequenceToken? token);
    }
}
