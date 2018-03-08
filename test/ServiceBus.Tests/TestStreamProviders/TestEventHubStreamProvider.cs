
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Tester.TestStreamProviders;
using Orleans;
using Orleans.Streams;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class TestEventHubStreamAdapterFactory : EventHubAdapterFactory
    {
        public TestEventHubStreamAdapterFactory(string name, EventHubOptions ehOptions, EventHubReceiverOptions receiverOptions, EventHubStreamCacheOptions cacheOptions, StreamStatisticOptions statisticOptions,
            IStreamQueueCheckpointerFactory checkpointerFactory, IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
            : base(name, ehOptions, receiverOptions, cacheOptions, statisticOptions, checkpointerFactory, serviceProvider, serializationManager, telemetryProducer, loggerFactory)
        {
            StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create(this.serviceProvider.GetRequiredService<SerializationManager>());
        }

        public static new TestEventHubStreamAdapterFactory Create(IServiceProvider services, string name)
        {
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCacheOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var checkpointerFactory = services.GetServiceByName<IStreamQueueCheckpointerFactory>(name);
            var factory = ActivatorUtilities.CreateInstance<TestEventHubStreamAdapterFactory>(services, name, ehOptions, receiverOptions, cacheOptions, statisticOptions, checkpointerFactory);
            factory.Init();
            return factory;
        }
    }
}
