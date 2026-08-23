#if NET10_0
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

[Collection(SQSStreamProviderBuilderTestCollection.CollectionName)]
[TestSuite("Functional")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS")]
public sealed class SQSAspireLiveStreamTests : TestClusterPerTest
{
    private const string ProviderName = "AspireSQS";
    private SqsAspireTestApp _app = null!;
    private EnvironmentVariableScope _environment = null!;
    private SingleStreamTestRunner _runner = null!;

    protected override void CheckPreconditionsOrThrow()
    {
        if (!AWSTestConstants.IsSqsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("SQS connection string is not configured.");
        }
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddEnvironmentVariables());
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    public override async ValueTask InitializeAsync()
    {
        EnsurePreconditionsMet();
        _app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [
                ("ServiceKey", "orleans-sqs"),
                ("PartitionCount", "4"),
                ("FifoQueue", "false"),
            ],
            [("ConnectionStrings:orleans-sqs", AWSTestConstants.SqsConnectionString)]);
        _environment = await _app.CreateEnvironmentScopeAsync(
            SqsAspireResourceRole.Silo,
            streamingOnly: true);
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

        try
        {
            if (HostedCluster is not null)
            {
                var clusterId = HostedCluster.Options.ClusterId;
                await base.DisposeAsync();
                await SQSStreamProviderUtils.DeleteAllUsedQueues(
                    ProviderName,
                    clusterId,
                    AWSTestConstants.SqsConnectionString,
                    NullLoggerFactory.Instance);
            }
        }
        finally
        {
            _environment?.Dispose();
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
            => siloBuilder.AddMemoryGrainStorage("PubSubStore");
    }
}
#endif
