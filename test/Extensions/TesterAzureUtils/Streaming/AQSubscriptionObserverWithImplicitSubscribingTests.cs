using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    public class AQSubscriptionObserverWithImplicitSubscribingTests : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<AQSubscriptionObserverWithImplicitSubscribingTests.Fixture>
    {
        private const int queueCount = 8;
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        private class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProviderName, sb=>sb.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}{StreamProviderName}", queueCount);
                        })).Configure<StreamPubSubOptions>(ob => ob.Configure(op => op.PubSubType = StreamPubSubType.ImplicitOnly)))
                    .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProviderName2, sb => sb.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}{StreamProviderName2}", queueCount);
                        })).Configure<StreamPubSubOptions>(ob => ob.Configure(op => op.PubSubType = StreamPubSubType.ImplicitOnly)))
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }
        
        public AQSubscriptionObserverWithImplicitSubscribingTests(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
        }
    }
}
