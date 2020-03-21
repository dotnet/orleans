
using Orleans.Streams;

namespace Orleans.Configuration
{
    public class SimpleMessageStreamProviderOptions
    {
        public bool FireAndForgetDelivery { get; set; } = DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY;
        public const bool DEFAULT_VALUE_FIRE_AND_FORGET_DELIVERY = false;

        public bool OptimizeForImmutableData { get; set; } = DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA;
        public const bool DEFAULT_VALUE_OPTIMIZE_FOR_IMMUTABLE_DATA = true;

        public StreamPubSubType PubSubType { get; set; } = DEFAULT_PUBSUB_TYPE;
        public static StreamPubSubType DEFAULT_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;
    }
}
