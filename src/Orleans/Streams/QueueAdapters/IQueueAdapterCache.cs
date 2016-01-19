namespace Orleans.Streams
{
    public interface IQueueAdapterCache
    {
        /// <summary>
        /// Create a cache for a given queue id
        /// </summary>
        /// <param name="messages"></param>
        IQueueCache CreateQueueCache(QueueId queueId);
    }
}
