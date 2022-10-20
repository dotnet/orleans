namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Configuration options for broadcast channel
    /// </summary>
    public class BroadcastChannelOptions
    {
        /// <summary>
        /// If set to true, the provider will not await calls to subscriber.
        /// </summary>
        public bool FireAndForgetDelivery { get; set; } = true;
    }
}

