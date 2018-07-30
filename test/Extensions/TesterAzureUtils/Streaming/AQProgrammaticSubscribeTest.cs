using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Functional"), TestCategory("AQStreaming")]
    public class AQProgrammaticSubscribeTest : ProgrammaticSubcribeTestsRunner, IClassFixture<AQProgrammaticSubscribeTest.Fixture>
    {
        private const int queueCount = 8;
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProviderName2, ob=>ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}2", queueCount);
                        }))
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProviderName, ob => ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
                    hostBuilder
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, 
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount), TestDefaultConfiguration.DataConnectionString).Wait();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}2", queueCount), TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        public AQProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }

}
