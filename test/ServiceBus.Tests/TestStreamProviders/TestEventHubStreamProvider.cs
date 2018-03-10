
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
using Orleans.ServiceBus.Providers.Testing;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class TestEventHubStreamAdapterFactory : EventHubAdapterFactory
    {
        public TestEventHubStreamAdapterFactory(string name, EventHubOptions ehOptions, EventHubReceiverOptions receiverOptions, EventHubStreamCachePressureOptions cacheOptions, 
            StreamCacheEvictionOptions evictionOptions, StreamStatisticOptions statisticOptions,
            IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
            : base(name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, serviceProvider, serializationManager, telemetryProducer, loggerFactory)
        {
            StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create(this.serviceProvider.GetRequiredService<SerializationManager>());
        }

        public static new TestEventHubStreamAdapterFactory Create(IServiceProvider services, string name)
        {
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            var factory = ActivatorUtilities.CreateInstance<TestEventHubStreamAdapterFactory>(services, name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions);
            factory.Init();
            return factory;
        }
    }
}
