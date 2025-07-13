using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
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

/// <summary>
/// Tests for SQL Server ADO.NET stream batching functionality.
/// </summary>
public class SqlServerAdoNetStreamsBatchingTests(ITestOutputHelper output) : AdoNetStreamsBatchingTests(new Fixture(AdoNetInvariants.InvariantNameSqlServer), output)
{
}

/// <summary>
/// Tests for MySQL ADO.NET stream batching functionality.
/// </summary>
public class MySqlAdoNetStreamsBatchingTests : AdoNetStreamsBatchingTests
{
    public MySqlAdoNetStreamsBatchingTests(ITestOutputHelper output) : base(new Fixture(AdoNetInvariants.InvariantNameMySql), output)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for PostgreSQL ADO.NET stream batching functionality.
/// </summary>
public class PostgreSqlAdoNetStreamsBatchingTests(ITestOutputHelper output) : AdoNetStreamsBatchingTests(new Fixture(AdoNetInvariants.InvariantNamePostgreSql), output)
{
}

/// <summary>
/// Base class for ADO.NET stream batching tests.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
public abstract class AdoNetStreamsBatchingTests : StreamBatchingTestRunner, IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private static RelationalStorageForTesting _testing;

    protected AdoNetStreamsBatchingTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
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
                            options.Invariant = _invariant;
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
                            options.Invariant = _invariant;
                            options.ConnectionString = _testing.CurrentConnectionString;
                        }));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    });
            }
        }
    }
}