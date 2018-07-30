using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UnitTests.StreamingTests;
using Xunit;
using Orleans.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming")]
    public class SampleAzureQueueStreamingTests : TestClusterPerTest
    {
        private const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private const int queueCount = 8;
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProvider, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }))
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }

        public override void Dispose()
        {
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, 
                AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount), 
                TestDefaultConfiguration.DataConnectionString).Wait();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SampleStreamingTests_4()
        {
            logger.Info("************************ SampleStreamingTests_4 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.HostedCluster);
            await runner.StreamingTests_Consumer_Producer(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task SampleStreamingTests_5()
        {
            logger.Info("************************ SampleStreamingTests_5 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.HostedCluster);
            await runner.StreamingTests_Producer_Consumer(Guid.NewGuid());
        }
    }
}
