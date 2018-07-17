using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Streaming.AzureStorage.Providers.Streams.AzureQueue;
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
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="azureQueueNames">azure queue names to be deleted.</param>
        /// <param name="storageConnectionString">The azure storage connection string.</param>
        public static async Task DeleteAllUsedAzureQueues(ILoggerFactory loggerFactory, List<string> azureQueueNames, string storageConnectionString)
        {
            var deleteTasks = new List<Task>();
            foreach (var queueName in azureQueueNames)
            {
                var manager = new AzureQueueDataManager(loggerFactory, queueName, storageConnectionString);
                deleteTasks.Add(manager.DeleteQueue());
            }

            await Task.WhenAll(deleteTasks);
        }

        /// <summary>
        /// Helper method for testing. Clears all messages in all the queues used by the specifed stream provider.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="azureQueueNames">The deployment ID hosting the stream provider.</param>
        /// <param name="storageConnectionString">The azure storage connection string.</param>
        public static async Task ClearAllUsedAzureQueues(ILoggerFactory loggerFactory, List<string> azureQueueNames, string storageConnectionString)
        {
            var deleteTasks = new List<Task>();
            foreach (var queueName in azureQueueNames)
            {
                var manager = new AzureQueueDataManager(loggerFactory, queueName, storageConnectionString);
                deleteTasks.Add(manager.ClearQueue());
            }

            await Task.WhenAll(deleteTasks);
        }
    }
}
