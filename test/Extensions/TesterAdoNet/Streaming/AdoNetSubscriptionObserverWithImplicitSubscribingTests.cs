using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.Tests.SqlUtils;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

[TestCategory("AdoNet"), TestCategory("Streaming"), TestCategory("Functional")]
public class AdoNetSubscriptionObserverWithImplicitSubscribingTests : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<AdoNetSubscriptionObserverWithImplicitSubscribingTests.Fixture>
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
            builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
            builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
        }
    }

    private class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddAdoNetStreams(StreamProviderName, sb =>
                {
                    sb.ConfigureAdoNet(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                    {
                        options.Invariant = AdoNetInvariantName;
                        options.ConnectionString = _testing.CurrentConnectionString;
                    }));
                    sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddAdoNetStreams(StreamProviderName2, sb =>
                {
                    sb.ConfigureAdoNet(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                    {
                        options.Invariant = AdoNetInvariantName;
                        options.ConnectionString = _testing.CurrentConnectionString;
                    }));
                    sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("PubSubStore");
        }

        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
    }

    public AdoNetSubscriptionObserverWithImplicitSubscribingTests(Fixture fixture) : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}