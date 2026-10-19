using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streams;
using OrleansAWSUtils.Storage;

namespace OrleansAWSUtils.Streams
{
    /// <summary>
    /// SQS utility functions
    /// </summary>
    public class SQSStreamProviderUtils
    {
        /// <summary>
        /// Async method to delete all used queues, for specific provider and clusterId
        /// </summary>
        /// <returns> Task object for this async method </returns>
        public static Task DeleteAllUsedQueues(string providerName, string clusterId, string storageConnectionString, ILoggerFactory loggerFactory)
            => DeleteAllUsedQueues(providerName, clusterId, storageConnectionString, loggerFactory, fifoQueue: false);

        /// <summary>
        /// Deletes the default number of queues used by a provider and cluster.
        /// </summary>
        public static Task DeleteAllUsedQueues(string providerName, string clusterId, string storageConnectionString, ILoggerFactory loggerFactory, bool fifoQueue)
            => DeleteAllUsedQueues(providerName, clusterId, storageConnectionString, loggerFactory, HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES, fifoQueue);

        /// <summary>
        /// Deletes the configured number of queues used by a provider and service.
        /// </summary>
        /// <param name="providerName">The stream provider name.</param>
        /// <param name="clusterId">The service ID used to prefix the provider's physical queue names.</param>
        /// <param name="storageConnectionString">The SQS connection string.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="partitionCount">The provider's configured total queue count.</param>
        /// <param name="fifoQueue">Whether the provider uses FIFO queues.</param>
        /// <returns>A task which completes when the configured queues have been deleted.</returns>
        public static async Task DeleteAllUsedQueues(string providerName, string clusterId, string storageConnectionString, ILoggerFactory loggerFactory, int partitionCount, bool fifoQueue)
        {
            if (clusterId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(
                    new HashRingStreamQueueMapperOptions { TotalQueueCount = partitionCount },
                    providerName);
                List<QueueId> allQueues = queueMapper.GetAllQueues().ToList();

                var sqsOptions = new SqsOptions
                {
                    ConnectionString = storageConnectionString,
                    FifoQueue = fifoQueue,
                };

                var deleteTasks = new List<Task>();
                foreach (var queueId in allQueues)
                {
                    var manager = new SQSStorage(loggerFactory, queueId.ToString(), sqsOptions, clusterId);
                    await manager.InitQueueAsync();
                    deleteTasks.Add(manager.DeleteQueue());
                }

                await Task.WhenAll(deleteTasks);
            }
        }
    }
}
