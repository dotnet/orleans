using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.General;
using UnitTests.StreamingTests;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for SQL Server ADO.NET subscription multiplicity.
/// </summary>
[TestCategory("SqlServer"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetSubscriptionMultiplicityTests() : AdoNetSubscriptionMultiplicityTests(AdoNetInvariants.InvariantNameSqlServer)
{
}

/// <summary>
/// Tests for MySQL ADO.NET subscription multiplicity.
/// </summary>
[TestCategory("MySql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("MySql")]
[TestSuite("Functional")]
public class MySqlAdoNetSubscriptionMultiplicityTests : AdoNetSubscriptionMultiplicityTests
{
    public MySqlAdoNetSubscriptionMultiplicityTests() : base(AdoNetInvariants.InvariantNameMySql)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for PostgreSQL ADO.NET subscription multiplicity.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("Functional"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
public class PostgreSqlAdoNetSubscriptionMultiplicityTests() : AdoNetSubscriptionMultiplicityTests(AdoNetInvariants.InvariantNamePostgreSql)
{
}

/// <summary>
/// Base class for ADO.NET subscription multiplicity tests.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestArea("Streaming")]
public abstract class AdoNetSubscriptionMultiplicityTests : TestClusterPerTest
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetStreamProviderName = "AdoNet";
    private const string StreamNamespace = "AdoNetSubscriptionMultiplicityTestsNamespace";

    private static string _invariant = null!;

    private static RelationalStorageForTesting _testing = null!;
    private SubscriptionMultiplicityTestRunner _runner = null!;

    protected AdoNetSubscriptionMultiplicityTests(string invariant)
    {
        _invariant = invariant;
        RelationalStorageForTesting.CheckPreconditionsOrThrow(_invariant);
    }

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

        // the runner must only be created after base initialization
        _runner = new SubscriptionMultiplicityTestRunner(AdoNetStreamProviderName, HostedCluster);
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
                });
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
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    [Fact, TestCategory("Functional")]
    public Task AdoNetMultipleParallelSubscriptionTest() => _runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetMultipleLinearSubscriptionTest() => _runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetMultipleSubscriptionTest_AddRemove() => _runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetResubscriptionTest() => _runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetResubscriptionAfterDeactivationTest() => _runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetActiveSubscriptionTest() => _runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetTwoIntermittentStreamTest() => _runner.TwoIntermittentStreamTest(Guid.NewGuid(), TestContext.Current.CancellationToken);

    [Fact, TestCategory("Functional")]
    public Task AdoNetSubscribeFromClientTest() => _runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);
}