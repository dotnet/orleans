using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using Tester.StreamingTests.Filtering;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

[TestCategory("AdoNet"), TestCategory("Streaming")]
[SuppressMessage("Blocker Code Smell", "S2699:Tests should include assertions", Justification = "N/A")]
public class AdoNetStreamFilteringTests : StreamFilteringTestsBase, IClassFixture<AdoNetStreamFilteringTests.Fixture>
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
    private const string AdoNetStreamProviderName = "AdoNet";
    private static RelationalStorageForTesting _testing;

    public AdoNetStreamFilteringTests(Fixture fixture) : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

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
                        options.Invariant = AdoNetInvariantName;
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
                        options.Invariant = AdoNetInvariantName;
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