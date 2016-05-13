using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Recieves batches of messages from a single partition of a message queue.  
    /// </summary>
    internal class AzureQueueAdapterReceiver : IQueueAdapterReceiver
    {
        private AzureQueueDataManager queue;
        private long lastReadMessage;
        private Task outstandingTask;
        private readonly TraceLogger logger;

        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(QueueId queueId, string dataConnectionString, string deploymentId)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");
            
            var queue = new AzureQueueDataManager(queueId.ToString(), deploymentId, dataConnectionString);
            return new AzureQueueAdapterReceiver(queueId, queue);
        }

        private AzureQueueAdapterReceiver(QueueId queueId, AzureQueueDataManager queue)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (queue == null) throw new ArgumentNullException("queue");
            
            Id = queueId;
            this.queue = queue;
            logger = TraceLogger.GetLogger(GetType().Name, TraceLogger.LoggerType.Provider);
        }

        public Task Initialize(TimeSpan timeout)
        {
            if (queue != null) // check in case we already shut it down.
            {
                return queue.InitQueueAsync();
            }
            return TaskDone.Done;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            try
            {
                // await the last storage operation, so after we shutdown and stop this receiver we don't get async operation completions from pending storage operations.
                if (outstandingTask != null)
                    await outstandingTask;
            }
            finally
            {
                // remember that we shut down so we never try to read from the queue again.
                queue = null;
            }
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                var queueRef = queue; // store direct ref, in case we are somehow asked to shutdown while we are receiving.    
                if (queueRef == null) return new List<IBatchContainer>();  

                int count = maxCount < 0 || maxCount == QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG ?
                    CloudQueueMessage.MaxNumberOfMessagesToPeek : Math.Min(maxCount, CloudQueueMessage.MaxNumberOfMessagesToPeek);

                var task = queueRef.GetQueueMessages(count);
                outstandingTask = task;
                IEnumerable<CloudQueueMessage> messages = await task;

                List<IBatchContainer> azureQueueMessages = messages
                    .Select(msg => (IBatchContainer)AzureQueueBatchContainer.FromCloudQueueMessage(msg, lastReadMessage++)).ToList();

                return azureQueueMessages;
            }
            finally
            {
                outstandingTask = null;
            }
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            try
            {
                var queueRef = queue; // store direct ref, in case we are somehow asked to shutdown while we are receiving.  
                if (messages.Count == 0 || queueRef==null) return;
                List<CloudQueueMessage> cloudQueueMessages = messages.Cast<AzureQueueBatchContainer>().Select(b => b.CloudQueueMessage).ToList();
                outstandingTask = Task.WhenAll(cloudQueueMessages.Select(queueRef.DeleteQueueMessage));
                try
                {
                    await outstandingTask;
                }
                catch (Exception exc)
                {
                    logger.Warn((int)ErrorCode.AzureQueue_15,
                        string.Format("Exception upon DeleteQueueMessage on queue {0}. Ignoring.", Id), exc);
                }
            }
            finally
            {
                outstandingTask = null;
            }
        }
    }
}
