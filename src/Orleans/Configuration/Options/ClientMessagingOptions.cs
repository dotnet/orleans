namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies global messaging options that are client related.
    /// </summary>
    public class ClientMessagingOptions : MessagingOptions
    {
        /// <summary>
        ///  The ClientSenderBuckets attribute specifies the total number of grain buckets used by the client in client-to-gateway communication
        ///  protocol. In this protocol, grains are mapped to buckets and buckets are mapped to gateway connections, in order to enable stickiness
        ///  of grain to gateway (messages to the same grain go to the same gateway, while evenly spreading grains across gateways).
        ///  This number should be about 10 to 100 times larger than the expected number of gateway connections.
        ///  If this attribute is not specified, then Math.Pow(2, 13) is used.
        /// </summary>
        public int ClientSenderBuckets { get; set; } = 8192;
    }
}
