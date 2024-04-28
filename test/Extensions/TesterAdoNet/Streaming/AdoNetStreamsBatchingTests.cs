using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.General;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using Xunit.Abstractions;
using static System.String;

namespace Tester.AdoNet.Streaming;

[TestCategory("AdoNet"), TestCategory("Streaming")]
public class AdoNetStreamsBatchingTests : StreamBatchingTestRunner, IClassFixture<AdoNetStreamsBatchingTests.Fixture>
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
    private static RelationalStorageForTesting _testing;

    public class Fixture : BaseTestClusterFixture
    {
        public override async Task InitializeAsync()
        {
            // set up the adonet environment before the base initializes
            _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);

            Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

            await base.InitializeAsync();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientBuilderConfigurator>();
        }

        private class TestSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder
                    .AddAdoNetStreams(StreamBatchingTestConst.ProviderName, sb =>
                    {
                        sb.ConfigureAdoNet(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.Invariant = AdoNetInvariantName;
                            options.ConnectionString = _testing.CurrentConnectionString;
                        }));
                        sb.ConfigurePullingAgent(ob => ob.Configure(options => options.BatchContainerBatchSize = 10));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    });
            }
        }

        private class TestClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddAdoNetStreams(StreamBatchingTestConst.ProviderName, sb =>
                    {
                        sb.ConfigureAdoNet(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.Invariant = AdoNetInvariantName;
                            options.ConnectionString = _testing.CurrentConnectionString;
                        }));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    });
            }
        }
    }

    public AdoNetStreamsBatchingTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
        fixture.EnsurePreconditionsMet();
    }
}