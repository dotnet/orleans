using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Hosting;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[TestSuite("Functional")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS")]
public sealed class SQSAspireLiveStreamTests : TestClusterPerTest
{
    private const string ProviderName = "AspireSQS";
    private SingleStreamTestRunner _runner = null!;

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        if (!AWSTestConstants.IsSqsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("SQS connection string is not configured.");
        }

        var configuration = new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "SQS",
            [$"Orleans:Streaming:{ProviderName}:ServiceKey"] = "orleans-sqs",
            [$"Orleans:Streaming:{ProviderName}:PartitionCount"] = "4",
            [$"Orleans:Streaming:{ProviderName}:FifoQueue"] = "false",
            ["ConnectionStrings:orleans-sqs"] = AWSTestConstants.SqsConnectionString,
        };
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(configuration));
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new SingleStreamTestRunner(InternalClient, ProviderName);
    }

    [Fact]
    public Task AspireGeneratedConfiguration_PublishesConsumesAndAcknowledgesElasticMqStream()
        => _runner.StreamTest_04_OneProducerClientOneConsumerClient();

    public override async ValueTask DisposeAsync()
    {
        if (!PreconditionsMet)
        {
            return;
        }

        var clusterId = HostedCluster.Options.ClusterId;
        await base.DisposeAsync();
        await SQSStreamProviderUtils.DeleteAllUsedQueues(
            ProviderName,
            clusterId,
            AWSTestConstants.SqsConnectionString,
            NullLoggerFactory.Instance);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
            => siloBuilder.AddMemoryGrainStorage("PubSubStore");
    }
}
