using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.General;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Cluster streaming tests for ADO.NET Streaming against SQL Server.
/// </summary>
public class SqlServerAdoNetStreamingTests() : AdoNetStreamingTests(AdoNetInvariants.InvariantNameSqlServer)
{
}

/// <summary>
/// Cluster streaming tests for ADO.NET Streaming against MySQL.
/// </summary>
public class MySqlAdoNetStreamingTests : AdoNetStreamingTests
{
    public MySqlAdoNetStreamingTests() : base(AdoNetInvariants.InvariantNameMySql)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Cluster streaming tests for ADO.NET Streaming against PostgreSQL.
/// </summary>
public class PostgreSqlAdoNetStreamingTests() : AdoNetStreamingTests(AdoNetInvariants.InvariantNamePostgreSql)
{
}

/// <summary>
/// Cluster streaming tests for ADO.NET Streaming.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
public abstract class AdoNetStreamingTests : TestClusterPerTest
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetStreamProviderName = "AdoNet";

    private static string _invariant;

    protected AdoNetStreamingTests(string invariant)
    {
        _invariant = invariant;
        RelationalStorageForTesting.CheckPreconditionsOrThrow(_invariant);
    }

    private static RelationalStorageForTesting _testing;
    private SingleStreamTestRunner _runner;

    public override async Task InitializeAsync()
    {
        // set up the adonet environment before the base initializes
        _testing = await RelationalStorageForTesting.SetupInstance(_invariant, TestDatabaseName);

        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        // base initialization must only happen after the above
        await base.InitializeAsync();

        // the runner must only be created after base initialization
        _runner = new SingleStreamTestRunner(InternalClient, AdoNetStreamProviderName);
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
                .AddAdoNetStreams(AdoNetStreamProviderName, options =>
                {
                    options.Invariant = _invariant;
                    options.ConnectionString = _testing.CurrentConnectionString;
                })
                .AddMemoryGrainStorage("MemoryStore")
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    private class TestClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddAdoNetStreams(AdoNetStreamProviderName, options =>
            {
                options.Invariant = _invariant;
                options.ConnectionString = _testing.CurrentConnectionString;
            });
        }
    }

    //------------------------ One to One -----------------------------------------------------//

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_01_OneProducerGrainOneConsumerGrain() => _runner.StreamTest_01_OneProducerGrainOneConsumerGrain();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_02_OneProducerGrainOneConsumerClient() => _runner.StreamTest_02_OneProducerGrainOneConsumerClient();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_03_OneProducerClientOneConsumerGrain() => _runner.StreamTest_03_OneProducerClientOneConsumerGrain();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_04_OneProducerClientOneConsumerClient() => _runner.StreamTest_04_OneProducerClientOneConsumerClient();

    //------------------------ MANY to Many different grains ----------------------------------//

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains() => _runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_06_ManyDifferent_ManyProducerGrainManyConsumerClients() => _runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_07_ManyDifferent_ManyProducerClientsManyConsumerGrains() => _runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_08_ManyDifferent_ManyProducerClientsManyConsumerClients() => _runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();

    //------------------------ MANY to Many Same grains ---------------------------------------//

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_09_ManySame_ManyProducerGrainsManyConsumerGrains() => _runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_10_ManySame_ManyConsumerGrainsManyProducerGrains() => _runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_11_ManySame_ManyProducerGrainsManyConsumerClients() => _runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_12_ManySame_ManyProducerClientsManyConsumerGrains() => _runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();

    //------------------------ MANY to Many producer consumer same grain ----------------------//

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_13_SameGrain_ConsumerFirstProducerLater() => _runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_14_SameGrain_ProducerFirstConsumerLater() => _runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);

    //-----------------------------------------------------------------------------------------//

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNet_15_ConsumeAtProducersRequest() => _runner.StreamTest_15_ConsumeAtProducersRequest();

    [SkippableFact, TestCategory("Functional")]
    public async Task AdoNet_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(InternalClient, AdoNetStreamProviderName, 16, false);

        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task AdoNet_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(InternalClient, AdoNetStreamProviderName, 17, false);

        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(() => HostedCluster.StartAdditionalSilo());
    }
}