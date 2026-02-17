using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Tester.TestStreamProviders.Controllable
{
    [GenerateSerializer]
    public enum ControllableTestStreamProviderCommands
    {
        AdapterEcho = PersistentStreamProviderCommand.AdapterCommandStartRange + 1,
        AdapterFactoryEcho = PersistentStreamProviderCommand.AdapterFactoryCommandStartRange + 1,
    }

    public class ControllableTestAdapterFactory : IQueueAdapter, IQueueAdapterFactory, IControllable
    {
        public string Name { get; private set; }

        public Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
            Dictionary<string, object> requestContext)
        {
            return Task.CompletedTask;
        }

        public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        {
            throw new NotImplementedException();
        }

        public bool IsRewindable { get; private set; }
        public StreamProviderDirection Direction { get; private set; }

        public ControllableTestAdapterFactory(string name)
        {
            Name = name;
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
            var options = new HashRingStreamQueueMapperOptions();
            options.TotalQueueCount = 0;
            return new HashRingBasedStreamQueueMapper(options, Name);
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
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        public static ControllableTestAdapterFactory Create(IServiceProvider services, string name)
        {
            var factory = ActivatorUtilities.CreateInstance<ControllableTestAdapterFactory>(services, name);
            return factory;
        }
    }
}
