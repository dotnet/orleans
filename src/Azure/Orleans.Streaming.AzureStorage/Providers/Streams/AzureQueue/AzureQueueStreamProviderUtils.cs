using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Utility functions for azure queue Persistent stream provider.
    /// </summary>
    public class AzureQueueStreamProviderUtils
    {
        /// <summary>
        /// Generate default azure queue names
        /// </summary>
        /// <param name="serviceId"></param>
        /// <param name="providerName"></param>
        /// <returns></returns>
        public static List<string> GenerateDefaultAzureQueueNames(string serviceId, string providerName)
        {
            var defaultQueueMapper = new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions(), providerName);
            return defaultQueueMapper.GetAllQueues()
                .Select(queueName => $"{serviceId}-{queueName}").ToList();
        }

        /// <summary>
        /// Helper method for testing. Deletes all the queues used by the specified stream provider.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="azureQueueNames">azure queue names to be deleted.</param>
        /// <param name="storageConnectionString">The azure storage connection string.</param>
        public static async Task DeleteAllUsedAzureQueues(ILoggerFactory loggerFactory, List<string> azureQueueNames, string storageConnectionString)
        {
            var options = new AzureQueueOptions();
            options.ConfigureQueueServiceClient(storageConnectionString);
            await DeleteAllUsedAzureQueues(loggerFactory, azureQueueNames, options);
        }

        /// <summary>
        /// Helper method for testing. Deletes all the queues used by the specified stream provider.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="azureQueueNames">azure queue names to be deleted.</param>
        /// <param name="queueOptions">The azure storage options.</param>
        public static async Task DeleteAllUsedAzureQueues(ILoggerFactory loggerFactory, List<string> azureQueueNames, AzureQueueOptions queueOptions)
        {
            var deleteTasks = new List<Task>();
            foreach (var queueName in azureQueueNames)
            {
                var manager = new AzureQueueDataManager(loggerFactory, queueName, queueOptions);
                deleteTasks.Add(manager.DeleteQueue());
            }

            await Task.WhenAll(deleteTasks);
        }

        /// <summary>
        /// Helper method for testing. Clears all messages in all the queues used by the specified stream provider.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="azureQueueNames">The deployment ID hosting the stream provider.</param>
        /// <param name="storageConnectionString">The azure storage connection string.</param>
        public static async Task ClearAllUsedAzureQueues(ILoggerFactory loggerFactory, List<string> azureQueueNames, string storageConnectionString)
        {
            var options = new AzureQueueOptions();
            options.ConfigureQueueServiceClient(storageConnectionString);
            await ClearAllUsedAzureQueues(loggerFactory, azureQueueNames, options);
        }

        /// <summary>
        /// Helper method for testing. Clears all messages in all the queues used by the specified stream provider.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="azureQueueNames">The deployment ID hosting the stream provider.</param>
        /// <param name="queueOptions">The azure storage options.</param>
        public static async Task ClearAllUsedAzureQueues(ILoggerFactory loggerFactory, List<string> azureQueueNames, AzureQueueOptions queueOptions)
        {
            var deleteTasks = new List<Task>();
            foreach (var queueName in azureQueueNames)
            {
                var manager = new AzureQueueDataManager(loggerFactory, queueName, queueOptions);
                deleteTasks.Add(manager.ClearQueue());
            }

            await Task.WhenAll(deleteTasks);
        }
    }
}
