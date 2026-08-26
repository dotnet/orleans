using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.General;
using static System.String;
using Xunit;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for SQL Server ADO.NET client stream functionality.
/// </summary>
[TestCategory("SqlServer"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetClientStreamTests(ITestOutputHelper output) : AdoNetClientStreamTests(AdoNetInvariants.InvariantNameSqlServer, output)
{
}

/// <summary>
/// Tests for MySQL ADO.NET client stream functionality.
/// </summary>
[TestCategory("MySql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("MySql")]
[TestSuite("Functional")]
public class MySqlAdoNetClientStreamTests : AdoNetClientStreamTests
{
    public MySqlAdoNetClientStreamTests(ITestOutputHelper output) : base(AdoNetInvariants.InvariantNameMySql, output)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for PostgreSQL ADO.NET client stream functionality.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
public class PostgreSqlAdoNetClientStreamTests(ITestOutputHelper output) : AdoNetClientStreamTests(AdoNetInvariants.InvariantNamePostgreSql, output)
{
}

/// <summary>
/// Base class for ADO.NET client stream tests.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestArea("Streaming")]
public abstract class AdoNetClientStreamTests : TestClusterPerTest
{
    protected AdoNetClientStreamTests(string invariant, ITestOutputHelper output)
    {
        _invariant = invariant;
        RelationalStorageForTesting.CheckPreconditionsOrThrow(_invariant);
        _output = output;
    }

    private static string _invariant = null!;
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetStreamProviderName = "AdoNet";
    private const string StreamNamespace = "AdoNetSubscriptionMultiplicityTestsNamespace";

    private readonly ITestOutputHelper _output;
    private static RelationalStorageForTesting _testing = null!;
    private ClientStreamTestRunner _runner = null!;

    public override async ValueTask InitializeAsync()
    {
        // set up the adonet environment before the base initializes
        _testing = await RelationalStorageForTesting.SetupInstance(
            _invariant,
            TestDatabaseName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.SkipWhen(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        // base initialization must only happen after the above
        await base.InitializeAsync();
        if (!PreconditionsMet)
        {
            return;
        }

        _runner = new ClientStreamTestRunner(HostedCluster);
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientBuilderConfigurator>();
    }

    private class TestClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder
                .AddAdoNetStreams(AdoNetStreamProviderName, options =>
                {
                    options.Invariant = _invariant;
                    options.ConnectionString = _testing.CurrentConnectionString;
                })
                .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
        }
    }

    private class TestSiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddAdoNetStreams(AdoNetStreamProviderName, options =>
                {
                    options.Invariant = _invariant;
                    options.ConnectionString = _testing.CurrentConnectionString;
                })
                .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5))
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    [Fact, TestCategory("Functional")]
    public Task AdoNetStreamProducerOnDroppedClientTest() => _runner.StreamProducerOnDroppedClientTest(
        AdoNetStreamProviderName,
        StreamNamespace,
        TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public virtual Task AdoNetStreamConsumerOnDroppedClientTest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        return _runner.StreamConsumerOnDroppedClientTest(
            AdoNetStreamProviderName,
            StreamNamespace,
            _output,
            async () => (await _testing.Storage.ReadAsync(
                "SELECT COUNT(*) FROM OrleansStreamDeadLetter",
                _ => { },
                (record, i, ct) => Task.FromResult(record.GetInt32(0)),
                cancellationToken: cancellationToken))
                .Single(),
            cancellationToken: cancellationToken);
    }
}