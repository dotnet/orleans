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
        /// Deletes all queues used by a provider and cluster.
        /// </summary>
        public static async Task DeleteAllUsedQueues(string providerName, string clusterId, string storageConnectionString, ILoggerFactory loggerFactory, bool fifoQueue)
        {
            if (clusterId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions(), providerName);
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
