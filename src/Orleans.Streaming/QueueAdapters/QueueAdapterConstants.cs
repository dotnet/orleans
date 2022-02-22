namespace Orleans.Streams
{
    /// <summary>
    /// Constants for queue adapters.
    /// </summary>
    public static class QueueAdapterConstants
    {
        /// <summary>
        /// The value used to indicate an unlimited number of messages can be retrieved, when returned by <see cref="IQueueFlowController.GetMaxAddCount"/>.
        /// </summary>
        public const int UNLIMITED_GET_QUEUE_MSG = -1;
    }
}
