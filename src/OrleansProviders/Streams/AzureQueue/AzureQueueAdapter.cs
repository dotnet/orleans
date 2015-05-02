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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.AzureUtils;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    internal class AzureQueueAdapter : IQueueAdapter
    {
        protected readonly string DeploymentId;
        protected readonly string DataConnectionString;
        private readonly HashRingBasedStreamQueueMapper streamQueueMapper;
        protected readonly ConcurrentDictionary<QueueId, AzureQueueDataManager> Queues = new ConcurrentDictionary<QueueId, AzureQueueDataManager>();

        public string Name { get ; private set; }
        public bool IsRewindable { get { return false; } }

        public StreamProviderDirection Direction { get { return StreamProviderDirection.ReadWrite; } }

        public AzureQueueAdapter(HashRingBasedStreamQueueMapper streamQueueMapper, string dataConnectionString, string deploymentId, string providerName)
        {
            if (String.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException("dataConnectionString");
            if (String.IsNullOrEmpty(deploymentId)) throw new ArgumentNullException("deploymentId");
            
            DataConnectionString = dataConnectionString;
            DeploymentId = deploymentId;
            Name = providerName;
            this.streamQueueMapper = streamQueueMapper;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            return AzureQueueAdapterReceiver.Create(queueId, DataConnectionString, DeploymentId);
        }

        public async Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events)
        {
            var queueId = streamQueueMapper.GetQueueForStream(streamGuid);
            AzureQueueDataManager queue;
            if (!Queues.TryGetValue(queueId, out queue))
            {
                var tmpQueue = new AzureQueueDataManager(queueId.ToString(), DeploymentId, DataConnectionString);
                await tmpQueue.InitQueueAsync();
                queue = Queues.GetOrAdd(queueId, tmpQueue);
            }
            var cloudMsg = AzureQueueBatchContainer.ToCloudQueueMessage(streamGuid, streamNamespace, events);
            await queue.AddQueueMessage(cloudMsg);
        }
    }
}
