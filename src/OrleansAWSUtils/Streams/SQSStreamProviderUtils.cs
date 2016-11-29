using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrleansAWSUtils.Streams
{
    /// <summary>
    /// SQS utility functions
    /// </summary>
    public class SQSStreamProviderUtils
    {
        /// <summary>
        /// Async method to delete all used queques, for specific provider and deploymentId
        /// </summary>
        /// <param name="providerName"></param>
        /// <param name="deploymentId"></param>
        /// <param name="storageConnectionString"></param>
        /// <returns> Task object for this async method </returns>
        public static async Task DeleteAllUsedQueues(string providerName, string deploymentId, string storageConnectionString)
        {
            if (deploymentId != null)
            {
                var queueMapper = new HashRingBasedStreamQueueMapper(SQSAdapterFactory.NumQueuesDefaultValue, providerName);
                List<QueueId> allQueues = queueMapper.GetAllQueues().ToList();

                var deleteTasks = new List<Task>();
                foreach (var queueId in allQueues)
                {
                    var manager = new SQSStorage(queueId.ToString(), storageConnectionString, deploymentId);
                    manager.InitQueueAsync().Wait();
                    deleteTasks.Add(manager.DeleteQueue());
                }

                await Task.WhenAll(deleteTasks);
            }
        }
    }
}
