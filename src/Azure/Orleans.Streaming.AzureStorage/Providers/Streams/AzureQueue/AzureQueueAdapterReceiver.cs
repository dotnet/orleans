using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Receives batches of messages from a single partition of a message queue.
    /// </summary>
    internal class AzureQueueAdapterReceiver : IQueueAdapterReceiver
    {
        private AzureQueueDataManager queue;
        private long lastReadMessage;
        private Task outstandingTask;
        private readonly ILogger logger;
        private readonly IQueueDataAdapter<string, IBatchContainer> dataAdapter;
        private readonly List<PendingDelivery> pending;

        private readonly string azureQueueName;

        public static IQueueAdapterReceiver Create(ILoggerFactory loggerFactory, string azureQueueName, AzureQueueOptions queueOptions, IQueueDataAdapter<string, IBatchContainer> dataAdapter)
        {
            if (azureQueueName == null) throw new ArgumentNullException(nameof(azureQueueName));
            if (queueOptions == null) throw new ArgumentNullException(nameof(queueOptions));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));

            var queue = new AzureQueueDataManager(loggerFactory, azureQueueName, queueOptions);
            return new AzureQueueAdapterReceiver(azureQueueName, loggerFactory, queue, dataAdapter);
        }

        private AzureQueueAdapterReceiver(string azureQueueName, ILoggerFactory loggerFactory, AzureQueueDataManager queue, IQueueDataAdapter<string, IBatchContainer> dataAdapter)
        {
            this.azureQueueName = azureQueueName ?? throw new ArgumentNullException(nameof(azureQueueName));
            this.queue = queue?? throw new ArgumentNullException(nameof(queue));
            this.dataAdapter = dataAdapter?? throw new ArgumentNullException(nameof(dataAdapter));
            this.logger = loggerFactory.CreateLogger<AzureQueueAdapterReceiver>();
            this.pending = new List<PendingDelivery>();
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
            const int MaxNumberOfMessagesToPeek = 32;

            try
            {
                var queueRef = queue; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
                if (queueRef == null) return new List<IBatchContainer>();

                int count = maxCount < 0 || maxCount == QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG ?
                    MaxNumberOfMessagesToPeek : Math.Min(maxCount, MaxNumberOfMessagesToPeek) ;

                var task = queueRef.GetQueueMessages(count);
                outstandingTask = task;
                IEnumerable<QueueMessage> messages = await task;

                List<IBatchContainer> azureQueueMessages = new List<IBatchContainer>();
                foreach (var message in messages)
                {
                    IBatchContainer container = this.dataAdapter.FromQueueMessage(message.MessageText, lastReadMessage++);
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
                List<QueueMessage> deliveredCloudQueueMessages = finalizedDeliveries
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
                    logger.LogWarning((int)AzureQueueErrorCode.AzureQueue_15,
                        exc,
                        "Exception upon DeleteQueueMessage on queue {QueueName}. Ignoring.", this.azureQueueName);
                }
            }
            finally
            {
                outstandingTask = null;
            }
        }

        private class PendingDelivery
        {
            public PendingDelivery(StreamSequenceToken token, QueueMessage message)
            {
                this.Token = token;
                this.Message = message;
            }

            public QueueMessage Message { get; }

            public StreamSequenceToken Token { get; }
        }
    }
}
