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
using System.Threading.Tasks;

using Orleans.Streams;
using Orleans.Runtime;

namespace Orleans.Providers.Streams.AzureQueue
{
    public class SimpleAzureQueueAdapterFactory : IQueueAdapterFactory
    {
        private string dataConnectionString;
        private string queueName;
        private string providerName;

        public const string DATA_CONNECTION_STRING = "DataConnectionString";
        public const string QUEUE_NAME_STRING = "QueueName";
        
        public virtual void Init(IProviderConfiguration config, string providerName, Logger logger)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (!config.Properties.TryGetValue(DATA_CONNECTION_STRING, out dataConnectionString))
                throw new ArgumentException(String.Format("{0} property not set", DATA_CONNECTION_STRING));
            if (!config.Properties.TryGetValue(QUEUE_NAME_STRING, out queueName))
                throw new ArgumentException(String.Format("{0} property not set", QUEUE_NAME_STRING));

            this.providerName = providerName;
        }

        public virtual Task<IQueueAdapter> CreateAdapter()
        {
            var adapter = new SimpleAzureQueueAdapter(dataConnectionString, providerName, queueName);
            return Task.FromResult<IQueueAdapter>(adapter);
        }

        public virtual IQueueAdapterCache GetQueueAdapterCache()
        {
            throw new OrleansException("SimpleAzureQueueAdapter is a write-only adapter, it does not support reading from the queue and thus does not need cache.");
        }

        public IStreamQueueMapper GetStreamQueueMapper()
        {
            throw new OrleansException("SimpleAzureQueueAdapter does not support multiple queues, it only writes to one queue.");
        }
    }
}
