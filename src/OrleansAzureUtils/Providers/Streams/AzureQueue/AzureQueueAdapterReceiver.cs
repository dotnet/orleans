using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Host.Providers.Streams.AzureQueue;
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
        private readonly Logger logger;
        private readonly bool hasLargeMessageSupport;

        private readonly Dictionary<Guid, List<MessageSegment>> messageSegments = new Dictionary<Guid, List<MessageSegment>>();

        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(QueueId queueId, string dataConnectionString, string deploymentId, bool hasLargeMesageSupport = false, TimeSpan? messageVisibilityTimeout = null)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");
            var queue = new AzureQueueDataManager(queueId.ToString(), deploymentId, dataConnectionString, messageVisibilityTimeout);
            return new AzureQueueAdapterReceiver(queueId, queue, hasLargeMesageSupport);
        }

        private AzureQueueAdapterReceiver(QueueId queueId, AzureQueueDataManager queue, bool hasLargeMessageSupport)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (queue == null) throw new ArgumentNullException("queue");
            this.hasLargeMessageSupport = hasLargeMessageSupport;
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
                    CloudQueueMessage.MaxNumberOfMessagesToPeek : Math.Min(maxCount, CloudQueueMessage.MaxNumberOfMessagesToPeek);

                var task = queueRef.GetQueueMessages(count);
                outstandingTask = task;
                IEnumerable<CloudQueueMessage> messages = await task;

                if (this.hasLargeMessageSupport)
                {
                    var list = new List<IBatchContainer>();
                    foreach (var segment in messages.Select(MessageSegment.FromCloudQueueMessage))
                    {
                        List<MessageSegment> segmentList;
                        if (segment.Count == 1)
                        {
                            list.Add(AzureQueueBatchContainer.FromMessageSegments(new[] {segment}, lastReadMessage++));
                            continue;
                        }

                        if (!messageSegments.TryGetValue(segment.Guid, out segmentList))
                        {
                            segmentList = new List<MessageSegment>();
                            messageSegments.Add(segment.Guid, segmentList);
                        }

                        // check to see if we have all the segments.
                        if (!Enumerable.Range(0, segment.Count).Except(segmentList.Select(s => (int) s.Index)).Any())
                        {
                            list.Add(AzureQueueBatchContainer.FromMessageSegments(segmentList, lastReadMessage++));
                            messageSegments.Remove(segment.Guid);
                        }
                    }

                    return list;
                }
                else
                {
                    return messages.Select(msg => (IBatchContainer)AzureQueueBatchContainer.FromCloudQueueMessage(msg, lastReadMessage++)).ToList();
                }
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
                var cloudQueueMessages = messages.Cast<AzureQueueBatchContainer>().SelectMany(b => b.CloudQueueMessages).ToList();
                outstandingTask = Task.WhenAll(cloudQueueMessages.Select(queueRef.DeleteQueueMessage));
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
    }
}
