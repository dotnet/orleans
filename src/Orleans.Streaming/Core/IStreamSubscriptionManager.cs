using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    /// <summary>
    /// Functionality for managing stream subscriptions.
    /// </summary>
    public interface IStreamSubscriptionManager
    {
        /// <summary>
        /// Subscribes the specified grain to a stream.
        /// </summary>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="grainRef">The grain reference.</param>
        /// <returns>The stream subscription.</returns>
        Task<StreamSubscription> AddSubscription(string streamProviderName, StreamId streamId, GrainReference grainRef);

        /// <summary>
        /// Unsubscribes a grain from a stream.
        /// </summary>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task RemoveSubscription(string streamProviderName, StreamId streamId, Guid subscriptionId);

        /// <summary>
        /// Gets the subscriptions for a stream.
        /// </summary>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <returns>The subscriptions.</returns>
        Task<IEnumerable<StreamSubscription>> GetSubscriptions(string streamProviderName, StreamId streamId);
    }
}
