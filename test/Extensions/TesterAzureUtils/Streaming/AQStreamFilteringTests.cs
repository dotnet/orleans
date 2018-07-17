using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
using UnitTests.StreamingTests;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
    public class StreamFilteringTests_AQ : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_AQ.Fixture>, IDisposable
    {
        private const int queueCount = 8;
        private readonly Fixture fixture;
        public class Fixture : BaseAzureTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
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
                        .AddMemoryGrainStorage("MemoryStore")
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount), 
                    TestDefaultConfiguration.DataConnectionString)
                    .Wait();
            }
        }

        public StreamFilteringTests_AQ(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.fixture = fixture;
            streamProviderName = Fixture.StreamProvider;
        }

        public virtual void Dispose()
        {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.fixture.HostedCluster.Options.ClusterId, queueCount),
                    TestDefaultConfiguration.DataConnectionString).Wait();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_Basic()
        {
            await Test_Filter_EvenOdd(true);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_EvenOdd()
        {
            await Test_Filter_EvenOdd();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_BadFunc()
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                Test_Filter_BadFunc());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_TwoObsv_Different()
        {
            await Test_Filter_TwoObsv_Different();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_Filter_TwoObsv_Same()
        {
            await Test_Filter_TwoObsv_Same();
        }
    }
}
