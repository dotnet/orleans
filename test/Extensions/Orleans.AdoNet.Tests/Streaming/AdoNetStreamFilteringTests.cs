using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using Tester.StreamingTests.Filtering;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for SQL Server ADO.NET stream filtering functionality.
/// </summary>
public class SqlServerAdoNetStreamFilteringTests() : AdoNetStreamFilteringTests(new Fixture(AdoNetInvariants.InvariantNameSqlServer))
{
}

/// <summary>
/// Tests for MySQL ADO.NET stream filtering functionality.
/// </summary>
public class MySqlAdoNetStreamFilteringTests : AdoNetStreamFilteringTests
{
    public MySqlAdoNetStreamFilteringTests() : base(new Fixture(AdoNetInvariants.InvariantNameMySql))
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for PostgreSQL ADO.NET stream filtering functionality.
/// </summary>
public class PostgreSqlAdoNetStreamFilteringTests() : AdoNetStreamFilteringTests(new Fixture(AdoNetInvariants.InvariantNamePostgreSql))
{
}

/// <summary>
/// Base class for ADO.NET stream filtering tests.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
public abstract class AdoNetStreamFilteringTests : StreamFilteringTestsBase, IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetStreamProviderName = "AdoNet";

    private static RelationalStorageForTesting _testing;

    protected AdoNetStreamFilteringTests(Fixture fixture) : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    public Task InitializeAsync() => fixture.InitializeAsync();

    public Task DisposeAsync() => fixture.DisposeAsync();

    public class Fixture : BaseTestClusterFixture
    {
        private static string _invariant;

        public Fixture(string invariant)
        {
            _invariant = invariant;
        }

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