namespace Orleans.Streaming.EventHubs
{
    /// <summary>
    /// Orleans ServiceBus error codes
    /// </summary>
    internal enum OrleansEventHubErrorCode
    {
        /// <summary>
        /// Start of orlean servicebus error codes
        /// </summary>
        ServiceBus = 1<<16,

        FailedPartitionRead = ServiceBus + 1,
        RetryReceiverInit   = ServiceBus + 2,
    }
}
