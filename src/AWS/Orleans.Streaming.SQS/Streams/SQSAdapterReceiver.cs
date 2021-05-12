using Orleans;
using Orleans.Streams;
using OrleansAWSUtils.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Streams
{
    /// <summary>
    /// Receives batches of messages from a single partition of a message queue.  
    /// </summary>
    internal class SQSAdapterReceiver : IQueueAdapterReceiver
    {
        private SQSStorage queue;
        private long lastReadMessage;
        private Task outstandingTask;
        private readonly ILogger logger;
        private readonly Serializer<SQSBatchContainer> serializer;


        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(Serializer<SQSBatchContainer> serializer, ILoggerFactory loggerFactory, QueueId queueId, string dataConnectionString, string serviceId)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));

            var queue = new SQSStorage(loggerFactory, queueId.ToString(), dataConnectionString, serviceId);
            return new SQSAdapterReceiver(serializer, loggerFactory, queueId, queue);
        }

        private SQSAdapterReceiver(Serializer<SQSBatchContainer> serializer, ILoggerFactory loggerFactory, QueueId queueId, SQSStorage queue)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (queue == null) throw new ArgumentNullException("queue");

            Id = queueId;
            this.queue = queue;
            logger = loggerFactory.CreateLogger<SQSAdapterReceiver>();
            this.serializer = serializer;
        }

        public Task Initialize(TimeSpan timeout)
        {
            if (queue != null) // check in case we already shut it down.
            {
                return queue.InitQueueAsync();
            }
            return Task.CompletedTask;
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

                List<IBatchContainer> messageBatch = messages
                    .Select(msg => (IBatchContainer)SQSBatchContainer.FromSQSMessage(this.serializer, msg, lastReadMessage++)).ToList();

                return messageBatch;
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
                    logger.LogWarning($"Exception upon DeleteMessage on queue {Id}. Ignoring.", exc);
                }
            }
            finally
            {
                outstandingTask = null;
            }
        }
    }
}
