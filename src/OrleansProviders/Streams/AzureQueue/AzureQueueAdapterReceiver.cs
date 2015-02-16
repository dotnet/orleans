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

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Recieves batches of messages from a single partition of a message queue.  
    /// </summary>
    internal class AzureQueueAdapterReceiver : IQueueAdapterReceiver
    {
        private readonly AzureQueueDataManager queue;
        private readonly IQueueAdapterCache cache;
        private long lastReadMessage;
        private Task outstandingTask;

        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(QueueId queueId, string dataConnectionString, string deploymentId, int cacheSize)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");
            if (cacheSize <= 0)
                throw new ArgumentOutOfRangeException("cacheSize", "CacheSize must be positive number.");
            
            var queue = new AzureQueueDataManager(queueId.ToString(), deploymentId, dataConnectionString);
            return new AzureQueueAdapterReceiver(queueId, queue, cacheSize);
        }

        private AzureQueueAdapterReceiver(QueueId queueId, AzureQueueDataManager queue, int cacheSize)
        {
            if (queueId == null) throw new ArgumentNullException("queueId");
            if (queue == null) throw new ArgumentNullException("queue");
            
            Id = queueId;
            this.queue = queue;
            cache = new SimpleQueueAdapterCache(cacheSize);
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

        public async Task<IEnumerable<IBatchContainer>> GetQueueMessagesAsync()
        {
            try
            {
                var task = queue.GetQueueMessages();
                outstandingTask = task;
                CloudQueueMessage[] messages = (await task).ToArray();
                if (!messages.Any())
                    return Enumerable.Empty<IBatchContainer>();
                
                AzureQueueBatchContainer[] azureQueueMessages = messages
                    .Select(msg => AzureQueueBatchContainer.FromCloudQueueMessage(msg, lastReadMessage++)).ToArray();

                outstandingTask = Task.WhenAll(messages.Select(queue.DeleteQueueMessage));
                await outstandingTask;

                return azureQueueMessages;
            }
            finally
            {
                outstandingTask = null;
            }
        }

        public void AddToCache(IEnumerable<IBatchContainer> messages)
        {
            if (messages == null) throw new ArgumentNullException("messages");
            
            foreach (AzureQueueBatchContainer message in messages.Cast<AzureQueueBatchContainer>())
                cache.Add(message, (EventSequenceToken)message.SequenceToken);
        }

        public IQueueAdapterCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token)
        {
            EventSequenceToken sequenceToken;
            if (token == null)
            {
                // Null token can come from a stream subscriber that is just interested to start consuming from latest (the most recent event added to the cache).
                sequenceToken = new EventSequenceToken(lastReadMessage);
            }
            else
            {
                var eventToken = token as EventSequenceToken;
                if (eventToken == null)
                    throw new ArgumentOutOfRangeException("token", "token must be of type EventSequenceToken");
                
                sequenceToken = eventToken;
            }

            return new SimpleQueueAdapterCacheCursor(cache, streamGuid, streamNamespace, sequenceToken);
        }
    }
}