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
    internal class AzureQueueAdapterReceiver: IQueueAdapterReceiver
    {
        private AzureQueueDataManager queue;
        private long lastReadMessage;
        private Task outstandingTask;
        private readonly Logger logger;
        private readonly IAzureQueueDataAdapter dataAdapter;
        private readonly List<PendingDelivery> pending;

        public QueueId Id { get; }

        public static IQueueAdapterReceiver Create(QueueId queueId, string dataConnectionString, string deploymentId, IAzureQueueDataAdapter dataAdapter, TimeSpan? messageVisibilityTimeout = null)
        {
            if (queueId == null) throw new ArgumentNullException(nameof(queueId));
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException(nameof(dataConnectionString));
            if (string.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException(nameof(deploymentId));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));

            var queue = new AzureQueueDataManager(queueId.ToString(), deploymentId, dataConnectionString, messageVisibilityTimeout);
            return new AzureQueueAdapterReceiver(queueId, queue, dataAdapter);
        }

        private AzureQueueAdapterReceiver(QueueId queueId, AzureQueueDataManager queue, IAzureQueueDataAdapter dataAdapter)
        {
            if (queueId == null) throw new ArgumentNullException(nameof(queueId));
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(queue));

            Id = queueId;
            this.queue = queue;
            this.dataAdapter = dataAdapter;
            this.logger = LogManager.GetLogger(GetType().Name, LoggerType.Provider);
            this.pending = new List<PendingDelivery>();
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

                List<IBatchContainer> azureQueueMessages = new List<IBatchContainer>();
                foreach (var message in messages)
                {
                    IBatchContainer container = this.dataAdapter.FromCloudQueueMessage(message, lastReadMessage++);
                    azureQueueMessages.Add(container);
                    this.pending.Add(new PendingDelivery(container.SequenceToken, message));
                }

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
                // get sequence tokens of delivered messages
                List<StreamSequenceToken> deliveredTokens = messages.Select(message => message.SequenceToken).ToList();
                // find oldest delivered message
                StreamSequenceToken oldest = deliveredTokens.Max();
                // finalize all pending messages at or befor the oldest
                List<PendingDelivery> finalizedDeliveries = pending
                    .Where(pendingDelivery => !pendingDelivery.Token.Newer(oldest))
                    .ToList();
                if (finalizedDeliveries.Count == 0) return;
                // remove all finalized deliveries from pending, regardless of if it was delivered or not.
                pending.RemoveRange(0, finalizedDeliveries.Count);
                // get the queue messages for all finalized deliveries that were delivered.
                List<CloudQueueMessage> deliveredCloudQueueMessages = finalizedDeliveries
                    .Where(finalized => deliveredTokens.Contains(finalized.Token))
                    .Select(finalized => finalized.Message)
                    .ToList();
                if (deliveredCloudQueueMessages.Count == 0) return;
                // delete all delivered queue messages from the queue.  Anything finalized but not delivered will show back up later
                outstandingTask = Task.WhenAll(deliveredCloudQueueMessages.Select(queueRef.DeleteQueueMessage));
                try
                {
                    await outstandingTask;
                }
                catch (Exception exc)
                {
                    logger.Warn(ErrorCode.AzureQueue_15,
                        $"Exception upon DeleteQueueMessage on queue {Id}. Ignoring.", exc);
                }
            }
            finally
            {
                outstandingTask = null;
            }
        }

        private class PendingDelivery
        {
            public PendingDelivery(StreamSequenceToken token, CloudQueueMessage message)
            {
                this.Token = token;
                this.Message = message;
            }

            public CloudQueueMessage Message { get; }

            public StreamSequenceToken Token { get; }
        }
    }
}
