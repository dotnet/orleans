using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Streaming.EventHubs;
using Orleans.TestingHost;
using Tester;
using Tester.StreamingTests;
using TestExtensions;

namespace ServiceBus.Tests.Streaming
{
    [TestCategory("Functional"), TestCategory("Streaming"), TestCategory("StreamingResume")]
    public class EHStreamingResumeTests : StreamingResumeTests
    {
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryGrainStorageAsDefault()
                    .AddEventHubStreams(StreamProviderName, b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options =>
                        {
                            options.StreamInactivityPeriod = StreamInactivityPeriod;
                        }));
                        b.ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubConnectionString, EHPath, EHConsumerGroup);
                        }));
                        b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                        {
                            options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                            options.PersistInterval = TimeSpan.FromSeconds(10);
                        }));
                        b.UseDataAdapter((sp, n) => ActivatorUtilities.CreateInstance<EventHubDataAdapter>(sp));
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
                            options.ConfigureEventHubConnection(TestDefaultConfiguration.EventHubConnectionString, EHPath, EHConsumerGroup);
                        }));
                    });
            }
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForEventHub();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForEventHub();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }
    }
}
