using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.AzureUtils;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Utility functions for azure queue Persistent stream provider.
    /// </summary>
    public class AzureQueueStreamProviderUtils
    {
        /// <summary>
        /// Helper method for testing. Deletes all the queues used by the specifed stream provider.
        /// </summary>
        /// <param name="providerName">The Azure Queue stream privider name.</param>
        /// <param name="deploymentId">The deployment ID hosting the stream provider.</param>
        /// <param name="storageConnectionString">The azure storage connection string.</param>
        public static async Task DeleteAllUsedAzureQueues(string providerName, string deploymentId, string storageConnectionString)
        {
            if (deploymentId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(AzureQueueAdapterConstants.NumQueuesDefaultValue, providerName);
                List<QueueId> allQueues = queueMapper.GetAllQueues().ToList();

                var deleteTasks = new List<Task>();
                foreach (var queueId in allQueues)
                {
                    var manager = new AzureQueueDataManager(queueId.ToString(), deploymentId, storageConnectionString);
                    deleteTasks.Add(manager.DeleteQueue());
                }

                await Task.WhenAll(deleteTasks);
            }
        }

        /// <summary>
        /// Helper method for testing. Clears all messages in all the queues used by the specifed stream provider.
        /// </summary>
        /// <param name="providerName">The Azure Queue stream privider name.</param>
        /// <param name="deploymentId">The deployment ID hosting the stream provider.</param>
        /// <param name="storageConnectionString">The azure storage connection string.</param>
        public static async Task ClearAllUsedAzureQueues(string providerName, string deploymentId, string storageConnectionString)
        {
            if (deploymentId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(AzureQueueAdapterConstants.NumQueuesDefaultValue, providerName);
                List<QueueId> allQueues = queueMapper.GetAllQueues().ToList();

                var deleteTasks = new List<Task>();
                foreach (var queueId in allQueues)
                {
                    var manager = new AzureQueueDataManager(queueId.ToString(), deploymentId, storageConnectionString);
                    deleteTasks.Add(manager.ClearQueue());
                }

                await Task.WhenAll(deleteTasks);
            }
        }
    }
}
