using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Factory responsible for creating a message cache for an EventHub partition.
    /// </summary>
    public interface IEventHubQueueCacheFactory
    {
        /// <summary>
        /// Function used to create a IEventHubQueueCache
        /// </summary>
        /// <param name="partition"></param>
        /// <param name="checkpointer"></param>
        /// <param name="cacheLogger"></param>
        /// <returns></returns>
        IEventHubQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, 
            Logger cacheLogger);
    }
}