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
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Tester.TestStreamProviders.Controllable
{
    public enum ControllableTestStreamProviderCommands
    {
        AdapterEcho = PersistentStreamProviderCommand.AdapterCommandStartRange + 1,
        AdapterFactoryEcho = PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 1,
    }

    public class ControllableTestStreamProvider : PersistentStreamProvider<ControllableTestStreamProvider.ControllableAdapterFactory>
    {
        public class ControllableAdapterFactory : IQueueAdapter, IQueueAdapterFactory, IControllable
        {
            public string Name { get; private set; }

            public Task QueueMessageBatchAsync<T>(Guid streamGuid, string streamNamespace, IEnumerable<T> events, StreamSequenceToken token,
                Dictionary<string, object> requestContext)
            {
                return TaskDone.Done;
            }

            public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
            {
                throw new NotImplementedException();
            }

            public bool IsRewindable { get; private set; }
            public StreamProviderDirection Direction { get; private set; }
            public void Init(IProviderConfiguration config, string providerName, Logger logger)
            {
                Name = providerName;
            }

            public Task<IQueueAdapter> CreateAdapter()
            {
                return Task.FromResult<IQueueAdapter>(this);
            }

            public IQueueAdapterCache GetQueueAdapterCache()
            {
                throw new NotImplementedException();
            }

            public IStreamQueueMapper GetStreamQueueMapper()
            {
                return new HashRingBasedStreamQueueMapper(0, Name);
            }

            public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
            {
                throw new NotImplementedException();
            }

            public Task<object> ExecuteCommand(int command, object arg)
            {
                switch ((ControllableTestStreamProviderCommands) command)
                {
                    case ControllableTestStreamProviderCommands.AdapterEcho:
                        return Task.FromResult<object>(Tuple.Create(ControllableTestStreamProviderCommands.AdapterEcho, arg));
                    case ControllableTestStreamProviderCommands.AdapterFactoryEcho:
                        return Task.FromResult<object>(Tuple.Create(ControllableTestStreamProviderCommands.AdapterFactoryEcho, arg));
                }
                throw new ArgumentOutOfRangeException("command");
            }
        }
    }
}
