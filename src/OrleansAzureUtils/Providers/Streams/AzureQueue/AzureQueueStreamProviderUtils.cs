using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.AzureUtils;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Utility functions for azure queue Persistent stream provider.
    /// </summary>
    public class AzureQueueStreamProviderUtils
    {
        /// <summary>
        /// Helper method for testing.
        /// </summary>
        /// <param name="providerName"></param>
        /// <param name="deploymentId"></param>
        /// <param name="storageConnectionString"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static async Task DeleteAllUsedAzureQueues(string providerName, string deploymentId, string storageConnectionString, Logger logger)
        {
            if (deploymentId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(AzureQueueAdapterFactory.DEFAULT_NUM_QUEUES, providerName);
                List<QueueId> allQueues = queueMapper.GetAllQueues().ToList();

                if (logger != null) logger.Info("About to delete all {0} Stream Queues\n", allQueues.Count);
                foreach (var queueId in allQueues)
                {
                    var manager = new AzureQueueDataManager(queueId.ToString(), deploymentId, storageConnectionString);
                    await manager.DeleteQueue();
                }
            }
        }
    }
}
