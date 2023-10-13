using Microsoft.Extensions.Configuration;
using Orleans.Providers;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    [TestCategory("BVT"), TestCategory("Streaming")]
    public class MemoryProgrammaticSubcribeTests : ProgrammaticSubcribeTestsRunner, IClassFixture<MemoryProgrammaticSubcribeTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
                builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
            }

            private class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    // Do use "PubSubStore" in this test

                    hostBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName);
                    hostBuilder.AddMemoryGrainStorage(StreamProviderName);

                    hostBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName2);
                    hostBuilder.AddMemoryGrainStorage(StreamProviderName2);
                }

                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
            }
        }

        public MemoryProgrammaticSubcribeTests(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
