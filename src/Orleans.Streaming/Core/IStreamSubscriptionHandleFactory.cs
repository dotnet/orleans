using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    /// <summary>
    /// Functionality for creating a stream subscription handle for a particular stream and subscription.
    /// </summary>
    public interface IStreamSubscriptionHandleFactory
    {
        /// <summary>
        /// Gets the stream identifier.
        /// </summary>
        /// <value>The stream identifier.</value>
        StreamId StreamId { get; }

        /// <summary>
        /// Gets the name of the provider.
        /// </summary>
        /// <value>The name of the provider.</value>
        string ProviderName { get; }

        /// <summary>
        /// Gets the subscription identifier.
        /// </summary>
        /// <value>The subscription identifier.</value>
        GuidId SubscriptionId { get; }

        /// <summary>
        /// Creates a stream subscription handle for the stream and subscription identified by this instance.
        /// </summary>
        /// <typeparam name="T">The stream element type.</typeparam>
        /// <returns>The new stream subscription handle.</returns>
        StreamSubscriptionHandle<T> Create<T>();
    }
}
