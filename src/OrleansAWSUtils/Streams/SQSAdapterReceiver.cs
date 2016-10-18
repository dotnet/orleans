using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Streams
{
    /// <summary>
    /// Recieves batches of messages from a single partition of a message queue.  
    /// </summary>
    internal class SQSAdapterReceiver : IQueueAdapterReceiver
    {
        private SQSStorage queue;
        private long lastReadMessage;
        private Task outstandingTask;
        private readonly Logger logger;
        

        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(QueueId queueId, string dataConnectionString, string deploymentId)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (string.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");

            var queue = new SQSStorage(queueId.ToString(), dataConnectionString, deploymentId);
            return new SQSAdapterReceiver(queueId, queue);
        }

        private SQSAdapterReceiver(QueueId queueId, SQSStorage queue)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (queue == null) throw new ArgumentNullException("queue");

            Id = queueId;
            this.queue = queue;
            logger = LogManager.GetLogger(GetType().Name, LoggerType.Provider);
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
                    SQSStorage.MAX_NUMBER_OF_MESSAGE_TO_PEAK : Math.Min(maxCount, SQSStorage.MAX_NUMBER_OF_MESSAGE_TO_PEAK);

                var task = queueRef.GetMessages(count);
                outstandingTask = task;
                IEnumerable<SQSMessage> messages = await task;

                List<IBatchContainer> azureQueueMessages = messages
                    .Select(msg => (IBatchContainer)SQSBatchContainer.FromSQSMessage(msg, lastReadMessage++)).ToList();

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
                if (messages.Count == 0 || queueRef == null) return;
                List<SQSMessage> cloudQueueMessages = messages.Cast<SQSBatchContainer>().Select(b => b.Message).ToList();
                outstandingTask = Task.WhenAll(cloudQueueMessages.Select(queueRef.DeleteMessage));
                try
                {
                    await outstandingTask;
                }
                catch (Exception exc)
                {
                    logger.Warn(ErrorCode.AzureQueue_15,
                        $"Exception upon DeleteMessage on queue {Id}. Ignoring.", exc);
                }
            }
            finally
            {
                outstandingTask = null;
            }
        }
    }
}
