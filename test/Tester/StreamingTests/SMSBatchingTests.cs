
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StreamingTests
{
    [TestCategory("BVT")]
    public class SMSBatchingTests : StreamBatchingTestRunner, IClassFixture<SMSBatchingTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                throw new SkipException("Batching not working on SMS yet");
            }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddClientBuilderConfigurator<ClientConfiguretor>();
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddSimpleMessageStreamProvider(StreamBatchingTestConst.ProviderName,
                        options => options.PubSubType = StreamPubSubType.ImplicitOnly);
                }
            }
            public class ClientConfiguretor : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddSimpleMessageStreamProvider(StreamBatchingTestConst.ProviderName,
                        options => options.PubSubType = StreamPubSubType.ImplicitOnly);
                }
            }
        }

        public SMSBatchingTests(Fixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
