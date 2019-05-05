using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.RabbitMQ.Providers
{
    /// <summary>
    /// Factory reference for creating a message cache for a RabbitMQ topic partition.
    /// </summary>
    public interface IRabbitMQQueueCacheFactory
    {
        /// <summary>
        /// Creates an implementation of <see cref="IRabbitMQQueueCache"/>
        /// </summary>
        /// <param name="partition"></param>
        /// <param name="checkpointer"></param>
        /// <param name="loggerFactory"></param>
        /// <param name="telemetryProducer"></param>
        /// <returns></returns>
        IRabbitMQQueueCache CreateCache(string partition, IStreamQueueCheckpointer<string> checkpointer, ILoggerFactory loggerFactory, ITelemetryProducer telemetryProducer);
    }
}
