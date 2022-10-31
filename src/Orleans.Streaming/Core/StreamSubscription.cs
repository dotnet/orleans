using System;
using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    /// <summary>
    /// Represents a subscription to a stream.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class StreamSubscription
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamSubscription"/> class.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamProviderName">Name of the stream provider.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="grainId">The grain identifier.</param>
        public StreamSubscription(Guid subscriptionId, string streamProviderName, StreamId streamId, GrainId grainId)
        {
            this.SubscriptionId = subscriptionId;
            this.StreamProviderName = streamProviderName;
            this.StreamId = streamId;
            this.GrainId = grainId;
        }

        /// <summary>
        /// Gets or sets the subscription identifier.
        /// </summary>
        /// <value>The subscription identifier.</value>
        [Id(0)]
        public Guid SubscriptionId { get; }

        /// <summary>
        /// Gets or sets the name of the stream provider.
        /// </summary>
        /// <value>The name of the stream provider.</value>
        [Id(1)]
        public string StreamProviderName { get; }

        /// <summary>
        /// Gets or sets the stream identifier.
        /// </summary>
        /// <value>The stream identifier.</value>
        [Id(2)]
        public StreamId StreamId { get; }

        /// <summary>
        /// Gets or sets the grain identifier.
        /// </summary>
        /// <value>The grain identifier.</value>
        [Id(3)]
        public GrainId GrainId { get; }
    }
}
