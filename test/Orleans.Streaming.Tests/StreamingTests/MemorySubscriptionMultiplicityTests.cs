using Microsoft.Extensions.Configuration;
using Orleans.Providers;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class MemorySubscriptionMultiplicityTests : IClassFixture<MemorySubscriptionMultiplicityTests.Fixture>
{
    private const string StreamProviderName = "MemorySubscriptionMultiplicity";
    private const string StreamNamespace = "MemorySubscriptionMultiplicityTests";
    private readonly SubscriptionMultiplicityTestRunner _runner;

    public MemorySubscriptionMultiplicityTests(Fixture fixture)
    {
        _runner = new SubscriptionMultiplicityTestRunner(StreamProviderName, fixture.HostedCluster);
    }

    [Fact]
    public Task MemoryMultipleSubscriptionTest_AddRemove()
        => _runner.MultipleSubscriptionTest_AddRemove(
            Guid.NewGuid(),
            StreamNamespace,
            TestContext.Current.CancellationToken);

    public sealed class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<Configurator>();
            builder.AddClientBuilderConfigurator<Configurator>();
        }

        private sealed class Configurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
                => siloBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName);

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                => clientBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName);
        }
    }
}
