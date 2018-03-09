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
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProviderName2, ob=>ob.Configure(options => options.ConnectionString = TestDefaultConfiguration.DataConnectionString))
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamProviderName, ob => ob.Configure(
                            options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            }));
                    hostBuilder
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

        public override void Dispose()
            {
                base.Dispose();
                var clusterId = this.HostedCluster?.Options.ClusterId;
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, StreamProviderName, clusterId, TestDefaultConfiguration.DataConnectionString).Wait();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, StreamProviderName2, clusterId, TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        public AQProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }

}
