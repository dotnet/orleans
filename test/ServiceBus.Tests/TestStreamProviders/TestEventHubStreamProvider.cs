
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Tester.TestStreamProviders;

namespace ServiceBus.Tests.TestStreamProviders.EventHub
{
    public class TestEventHubStreamAdapterFactory : EventHubAdapterFactory
    {
        public TestEventHubStreamAdapterFactory(string name, EventHubStreamOptions options, IServiceProvider serviceProvider, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory)
            : base(name, options, serviceProvider, serializationManager, telemetryProducer, loggerFactory)
        {
            StreamFailureHandlerFactory = qid => TestAzureTableStorageStreamFailureHandler.Create(this.serviceProvider.GetRequiredService<SerializationManager>());
        }

        public static new TestEventHubStreamAdapterFactory Create(IServiceProvider services, string name)
        {
            IOptionsSnapshot<EventHubStreamOptions> streamOptionsSnapshot = services.GetRequiredService<IOptionsSnapshot<EventHubStreamOptions>>();
            var factory = ActivatorUtilities.CreateInstance<TestEventHubStreamAdapterFactory>(services, name, streamOptionsSnapshot.Get(name));
            factory.Init();
            return factory;
        }
    }
}
