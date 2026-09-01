
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// This interface generalizes the standard .NET IObserveable interface to allow asynchronous consumption of items.
    /// Asynchronous here means that the consumer can process items asynchronously and signal item completion to the 
    /// producer by completing the returned Task.
    /// <para>
    /// Note that this interface is invoked (used) by item consumers and implemented by item producers.
    /// This means that the producer endpoint of a stream implements this interface.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type of object produced by the observable.</typeparam>
    public interface IAsyncObservable<T>
    {
        /// <summary>
        /// Subscribe a consumer to this observable.
        /// </summary>
        /// <param name="observer">The asynchronous observer to subscribe.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.
        /// </returns>
        Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer);

        /// <summary>
        /// Subscribe a consumer to this observable using the specified initial position.
        /// </summary>
        /// <param name="observer">The asynchronous observer to subscribe.</param>
        /// <param name="options">The subscription options.</param>
        /// <param name="filterData">Data object that will be passed in to the filter.</param>
        /// <returns>A promise for a <see cref="StreamSubscriptionHandle{T}"/> that represents the subscription.</returns>
        /// <remarks>
        /// <see cref="StreamSubscriptionStartPosition.EarliestAvailable"/> is supported by rewindable persistent streams
        /// whose local queue cache supports start-position selection.
        /// </remarks>
        Task<StreamSubscriptionHandle<T>> SubscribeWithOptionsAsync(
            IAsyncObserver<T> observer,
            StreamSubscriptionOptions options,
            string? filterData = null)
        {
            options.Validate();
            if (options.StartPosition == StreamSubscriptionStartPosition.Latest)
            {
                return SubscribeAsync(observer, null, filterData);
            }

            throw new NotSupportedException(
                $"{GetType().FullName} does not support {StreamSubscriptionStartPosition.EarliestAvailable} subscriptions.");
        }

        /// <summary>
        /// Subscribe a consumer to this observable.
        /// </summary>
        /// <param name="observer">The asynchronous observer to subscribe.</param>
        /// <param name="token">The stream sequence to be used as an offset to start the subscription from.</param>
        /// <param name="filterData">Data object that will be passed in to the filter.</param>
        /// <returns>A promise for a StreamSubscriptionHandle that represents the subscription.
        /// The consumer may unsubscribe by using this handle.
        /// The subscription remains active for as long as it is not explicitly unsubscribed.
        /// </returns>
        Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer, StreamSequenceToken? token, string? filterData = null);
    }
}
