using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrleansAWSUtils.Streams
{
    public class SQSStreamProviderUtils
    {
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
