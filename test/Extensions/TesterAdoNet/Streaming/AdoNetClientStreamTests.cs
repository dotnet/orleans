using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet.Storage;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.General;
using Xunit.Abstractions;
using static System.String;

namespace Tester.AdoNet.Streaming;

[TestCategory("AdoNet"), TestCategory("Streaming")]
public class AdoNetClientStreamTests(ITestOutputHelper output) : TestClusterPerTest
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
    private const string AdoNetStreamProviderName = "AdoNet";
    private const string StreamNamespace = "AdoNetSubscriptionMultiplicityTestsNamespace";

    private readonly ITestOutputHelper _output = output;
    private static RelationalStorageForTesting _testing;
    private ClientStreamTestRunner _runner;

    public override async Task InitializeAsync()
    {
        // set up the adonet environment before the base initializes
        _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);

        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        // base initialization must only happen after the above
        await base.InitializeAsync();

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
                    options.Invariant = AdoNetInvariantName;
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
                    options.Invariant = AdoNetInvariantName;
                    options.ConnectionString = _testing.CurrentConnectionString;
                })
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    [SkippableFact, TestCategory("Functional")]
    public Task AdoNetStreamProducerOnDroppedClientTest() => _runner.StreamProducerOnDroppedClientTest(AdoNetStreamProviderName, StreamNamespace);

    [SkippableFact, TestCategory("Functional")]
    public async Task AdoNetStreamConsumerOnDroppedClientTest()
    {
        await _runner.StreamConsumerOnDroppedClientTest(
            AdoNetStreamProviderName,
            StreamNamespace,
            _output,
            async () => (await _testing.Storage.ReadAsync(
                "SELECT COUNT(*) AS [Count] FROM [OrleansStreamDeadLetter]",
                _ => { },
                (record, i, ct) => Task.FromResult((int)record["Count"]))).Single());
    }
}