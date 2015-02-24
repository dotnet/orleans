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
using System.Collections.Concurrent;
using System.Collections.Generic;
﻿using System.Linq;
﻿using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.AzureUtils;
﻿using Orleans.Runtime;
﻿using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class SimpleAzureQueueAdapter : IQueueAdapter
    {
        protected readonly string DataConnectionString;
        protected readonly string QueueName;
        protected AzureQueueDataManager Queue;

        public string Name { get ; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.WriteOnly; } }

        public SimpleAzureQueueAdapter(string dataConnectionString, string providerName, string queueName)
        {
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(queueName)) throw new ArgumentNullException("queueName");

            DataConnectionString = dataConnectionString;
            Name = providerName;
            QueueName = queueName;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            throw new OrleansException("SimpleAzureQueueAdapter is a write-only adapter, it does not support reading from the queue.");
        }

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            throw new OrleansException("SimpleAzureQueueAdapter does not support multiple queues, it only writes to one queue.");
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events)
        {
            if (events == null)
            {
                throw new ArgumentNullException("Trying to QueueMessageBatchAsync null data.");
            }
            int count = events.Count();
            if (count != 1)
            {
                throw new OrleansException("Trying to QueueMessageBatchAsync a batch of more than one event. " +
                                           "SimpleAzureQueueAdapter does not support batching. Instead, you can batch in your application code.");
            }
            object data = events.First();
            bool isBytes = data is byte[];
            bool isString = data is string;
            if (data != null && !isBytes && !isString)
            {
                throw new OrleansException(
                    string.Format(
                        "Trying to QueueMessageBatchAsync a type {0} which is not a byte[] and not string. " +
                        "SimpleAzureQueueAdapter only supports byte[] or string.", data.GetType()));
            }

            if (Queue == null)
            {
                var tmpQueue = new AzureQueueDataManager(QueueName, DataConnectionString);
                await tmpQueue.InitQueueAsync();
                if (Queue == null)
                {
                    Queue = tmpQueue;
                }
            }
            CloudQueueMessage cloudMsg = null;
            if (isBytes)
            {
                cloudMsg = new CloudQueueMessage(data as byte[]);
            }else if (isString)
            {
                cloudMsg = new CloudQueueMessage(data as string);
            }else if (data == null)
            {
                // It's OK to pass null data. why should I care?
                cloudMsg = new CloudQueueMessage(null as byte[]);
            }
            await Queue.AddQueueMessage(cloudMsg);
        }
    }
}