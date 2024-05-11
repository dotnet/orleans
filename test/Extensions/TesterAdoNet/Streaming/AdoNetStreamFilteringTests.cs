using Microsoft.Extensions.Configuration;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using Tester.StreamingTests.Filtering;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

public class SqlServerAdoNetStreamFilteringTests(AdoNetStreamFilteringTests.Fixture fixture) : AdoNetStreamFilteringTests(AdoNetInvariants.InvariantNameSqlServer, fixture), IClassFixture<AdoNetStreamFilteringTests.Fixture>
{
}

public class MySqlAdoNetStreamFilteringTests(AdoNetStreamFilteringTests.Fixture fixture) : AdoNetStreamFilteringTests(AdoNetInvariants.InvariantNameMySql, fixture), IClassFixture<AdoNetStreamFilteringTests.Fixture>
{
}

[TestCategory("AdoNet"), TestCategory("Streaming")]
public abstract class AdoNetStreamFilteringTests : StreamFilteringTestsBase
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetStreamProviderName = "AdoNet";

    private static RelationalStorageForTesting _testing;
    private static string _invariant = AdoNetInvariants.InvariantNameSqlServer;

    protected AdoNetStreamFilteringTests(string invariant, Fixture fixture) : base(fixture)
    {
        _invariant = invariant;

        fixture.EnsurePreconditionsMet();
    }

    public class Fixture : BaseTestClusterFixture
    {
        public override async Task InitializeAsync()
        {
            // set up the adonet environment before the base initializes
            _testing = await RelationalStorageForTesting.SetupInstance(_invariant, TestDatabaseName);

            Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

            await base.InitializeAsync();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        }

        public class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder
                    .AddAdoNetStreams(AdoNetStreamProviderName, options =>
                    {
                        options.Invariant = _invariant;
                        options.ConnectionString = _testing.CurrentConnectionString;
                    })
                    .AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddStreamFilter<CustomStreamFilter>(AdoNetStreamProviderName);
            }
        }

        public class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddAdoNetStreams(AdoNetStreamProviderName, options =>
                    {
                        options.Invariant = _invariant;
                        options.ConnectionString = _testing.CurrentConnectionString;
                    })
                    .AddStreamFilter<CustomStreamFilter>(AdoNetStreamProviderName);
            }
        }
    }

    protected override string ProviderName => AdoNetStreamProviderName;

    protected override TimeSpan WaitTime => TimeSpan.FromSeconds(2);

    [SkippableFact, TestCategory("BVT"), TestCategory("Filters")]
    public override Task IgnoreBadFilter() => base.IgnoreBadFilter();

    [SkippableFact, TestCategory("BVT"), TestCategory("Filters")]
    public override Task OnlyEvenItems() => base.OnlyEvenItems();

    [SkippableFact, TestCategory("BVT"), TestCategory("Filters")]
    public override Task MultipleSubscriptionsDifferentFilterData() => base.MultipleSubscriptionsDifferentFilterData();
}