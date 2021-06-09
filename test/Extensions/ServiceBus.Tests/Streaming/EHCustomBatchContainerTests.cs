using System;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.TestingHost;
using ServiceBus.Tests.TestStreamProviders.EventHub;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Streams;
using Orleans.ServiceBus.Providers;
using Tester;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Serialization;
using Orleans.Providers.Streams.Common;
using System.Collections.Generic;
using Xunit;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;
using Tester.StreamingTests;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional"), TestCategory("StreamingCacheMiss")]
    public class EHCustomBatchContainerTests : StreamingCacheMissTests
    {
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        public EHCustomBatchContainerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForEventHub();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        #region Configuration stuff

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddEventHubStreams(StreamProviderName, b =>
                    {
                        b.ConfigureCacheEviction(ob => ob.Configure(options =>
                        {
                            options.DataMaxAgeInCache = TimeSpan.FromSeconds(5);
                            options.DataMinTimeInCache = TimeSpan.FromSeconds(0);
                            options.MetadataMinTimeInCache = TimeSpan.FromMinutes(1);
                        }));
                        b.ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        }));
                        b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.PersistInterval = TimeSpan.FromSeconds(10);
                        }));
                        b.UseDataAdapter((sp, n) => ActivatorUtilities.CreateInstance<CustomDataAdapter>(sp));
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddEventHubStreams(StreamProviderName, b =>
                    {
                        b.ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        }));
                        b.UseDataAdapter((sp, n) => ActivatorUtilities.CreateInstance<CustomDataAdapter>(sp));
                    });
            }
        }

        private class CustomDataAdapter : EventHubDataAdapter
        {
            public CustomDataAdapter(SerializationManager serializationManager)
                : base(serializationManager)
            {
            }

            public override IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage)
                => new CustomBatchContainer(base.GetBatchContainer(ref cachedMessage));

            protected override IBatchContainer GetBatchContainer(EventHubMessage eventHubMessage)
                => new CustomBatchContainer(base.GetBatchContainer(eventHubMessage));
        }

        #endregion
    }
}
