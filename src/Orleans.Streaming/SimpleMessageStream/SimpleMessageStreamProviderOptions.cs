
using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for the Simple Message Stream provider.
    /// </summary>
    public class SimpleMessageStreamProviderOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether delivery should be fire and forget.
        /// </summary>
        /// <value><see langword="true" /> if delivery should be fire and forget; otherwise, <see langword="false" />.</value>
        public bool FireAndForgetDelivery { get; set; } = DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY;
        public const bool DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY = false;

        /// <summary>
        /// Gets or sets a value indicating whether to optimize for immutable data.
        /// </summary>
        public bool OptimizeForImmutableData { get; set; } = DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA;
        public const bool DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA = true;

        /// <summary>
        /// Gets or sets the type of the pub sub to use.
        /// </summary>
        public StreamPubSubType PubSubType { get; set; } = DEFAULT_PUBSUB_TYPE;

        /// <summary>
        /// The default <see cref="PubSubType"/>.
        /// </summary>
        public static StreamPubSubType DEFAULT_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;
    }
}
