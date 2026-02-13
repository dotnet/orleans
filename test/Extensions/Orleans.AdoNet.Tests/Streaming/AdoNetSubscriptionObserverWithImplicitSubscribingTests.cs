using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.Tests.SqlUtils;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for SQL Server ADO.NET subscription observer with implicit subscribing.
/// </summary>
public class SqlServerAdoNetSubscriptionObserverWithImplicitSubscribingTests() : AdoNetSubscriptionObserverWithImplicitSubscribingTests(new Fixture(AdoNetInvariants.InvariantNameSqlServer))
{
}

/// <summary>
/// Tests for MySQL ADO.NET subscription observer with implicit subscribing.
/// </summary>
public class MySqlAdoNetSubscriptionObserverWithImplicitSubscribingTests : AdoNetSubscriptionObserverWithImplicitSubscribingTests
{
    public MySqlAdoNetSubscriptionObserverWithImplicitSubscribingTests() : base(new Fixture(AdoNetInvariants.InvariantNameMySql))
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for PostgreSQL ADO.NET subscription observer with implicit subscribing.
/// </summary>
public class PostgreSqlAdoNetSubscriptionObserverWithImplicitSubscribingTests() : AdoNetSubscriptionObserverWithImplicitSubscribingTests(new Fixture(AdoNetInvariants.InvariantNamePostgreSql))
{
}

/// <summary>
/// Base class for ADO.NET subscription observer with implicit subscribing tests.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming"), TestCategory("Functional")]
public abstract class AdoNetSubscriptionObserverWithImplicitSubscribingTests(AdoNetSubscriptionObserverWithImplicitSubscribingTests.Fixture fixture) : SubscriptionObserverWithImplicitSubscribingTestRunner(fixture), IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private static RelationalStorageForTesting _testing;
    private readonly Fixture _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.EnsurePreconditionsMet();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

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
            builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
            builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
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
                            options.Invariant = _invariant;
                            options.ConnectionString = _testing.CurrentConnectionString;
                        }));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    })
                    .AddAdoNetStreams(StreamProviderName2, sb =>
                    {
                        sb.ConfigureAdoNet(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.Invariant = _invariant;
                            options.ConnectionString = _testing.CurrentConnectionString;
                        }));
                        sb.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    })
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore");
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
        }
    }
}