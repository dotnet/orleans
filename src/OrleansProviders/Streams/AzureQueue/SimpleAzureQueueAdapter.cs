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
using System.Threading.Tasks;
using Microsoft.WindowsAzure.StorageClient;
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
            throw new OrleansException("SimpleAzureQueueAdapter does not support multipel queues, it only writes to one queue.");
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events)
        {
            if (Queue == null)
            {
                var tmpQueue = new AzureQueueDataManager(QueueName, DataConnectionString);
                await tmpQueue.InitQueueAsync();
                if (Queue == null)
                {
                    Queue = tmpQueue;
                }
            }
            byte[] rawBytes = null; //(byte[])events;
            var cloudMsg = new CloudQueueMessage(rawBytes);
            await Queue.AddQueueMessage(cloudMsg);
        }
    }
}