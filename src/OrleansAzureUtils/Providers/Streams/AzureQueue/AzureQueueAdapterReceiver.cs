/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Recieves batches of messages from a single partition of a message queue.  
    /// </summary>
    internal class AzureQueueAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly AzureQueueDataManager queue;
        private long lastReadMessage;
        private Task outstandingTask;

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
        }

        public Task Initialize(TimeSpan timeout)
        {
            return queue.InitQueueAsync();
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            // await the last storage operation, so after we shutdown and stop this receiver we don't get async operation completions from pending storage operations.
            if (outstandingTask != null)
                await outstandingTask;
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                int count = maxCount < 0 || maxCount == QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG ? 
                    CloudQueueMessage.MaxNumberOfMessagesToPeek : Math.Min(maxCount, CloudQueueMessage.MaxNumberOfMessagesToPeek);

                var task = queue.GetQueueMessages(count);
                outstandingTask = task;
                IEnumerable<CloudQueueMessage> messages = await task;
                
                List<IBatchContainer> azureQueueMessages = messages
                    .Select(msg => (IBatchContainer)AzureQueueBatchContainer.FromCloudQueueMessage(msg, lastReadMessage++)).ToList();

                if (azureQueueMessages.Count == 0)
                    return azureQueueMessages;

                outstandingTask = Task.WhenAll(messages.Select(queue.DeleteQueueMessage));
                await outstandingTask;

                return azureQueueMessages;
            }
            finally
            {
                outstandingTask = null;
            }
        }
    }
}
