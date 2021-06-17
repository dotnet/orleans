
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans;
using Orleans.Streams;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class TestEventHubStreamAdapterFactory : EventHubAdapterFactory
    {
        public TestEventHubStreamAdapterFactory(
            string name,
            EventHubOptions ehOptions,
            EventHubReceiverOptions receiverOptions,
            EventHubStreamCachePressureOptions cacheOptions,
            StreamCacheEvictionOptions evictionOptions,
            StreamStatisticOptions statisticOptions,
            IEventHubDataAdapter dataAdapter,
            IServiceProvider serviceProvider,
            ITelemetryProducer telemetryProducer,
            ILoggerFactory loggerFactory)
            : base(name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, dataAdapter, serviceProvider, telemetryProducer, loggerFactory)
        {
            StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create(this.serviceProvider.GetRequiredService<Serializer<StreamSequenceToken>>());
        }

        public static new TestEventHubStreamAdapterFactory Create(IServiceProvider services, string name)
        {
            var ehOptions = services.GetOptionsByName<EventHubOptions>(name);
            var receiverOptions = services.GetOptionsByName<EventHubReceiverOptions>(name);
            var cacheOptions = services.GetOptionsByName<EventHubStreamCachePressureOptions>(name);
            var evictionOptions = services.GetOptionsByName<StreamCacheEvictionOptions>(name);
            var statisticOptions = services.GetOptionsByName<StreamStatisticOptions>(name);
            IEventHubDataAdapter dataAdapter = services.GetServiceByName<IEventHubDataAdapter>(name)
                ?? services.GetService<IEventHubDataAdapter>()
                ?? ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services);
            var factory = ActivatorUtilities.CreateInstance<TestEventHubStreamAdapterFactory>(services, name, ehOptions, receiverOptions, cacheOptions, evictionOptions, statisticOptions, dataAdapter);
            factory.Init();
            return factory;
        }
    }
}
