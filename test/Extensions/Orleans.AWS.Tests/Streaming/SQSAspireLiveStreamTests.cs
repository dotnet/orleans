#if NET10_0
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Hosting;
using Orleans.Persistence.FileStorage;
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
    private const string PubSubStoreRootDirectoryKey = "AspireSQS:PubSubStoreRootDirectory";
    private SqsAspireTestApp _app = null!;
    private EnvironmentVariableScope _environment = null!;
    private string? _ownedDirectory;
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
        var ownedDirectory = _ownedDirectory
            ?? throw new InvalidOperationException("The PubSubStore directory has not been initialized.");
        builder.Options.InitialSilosCount = 1;
        builder.Properties[PubSubStoreRootDirectoryKey] = Path.Combine(
            ownedDirectory,
            "PubSubStore");
        builder.ConfigureHostConfiguration(configuration => configuration.AddEnvironmentVariables());
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    public override async ValueTask InitializeAsync()
    {
        EnsurePreconditionsMet();
        _ownedDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(SQSAspireLiveStreamTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_ownedDirectory);
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
                var serviceId = HostedCluster.Options.ServiceId;
                await base.DisposeAsync();
                await SQSStreamProviderUtils.DeleteAllUsedQueues(
                    ProviderName,
                    serviceId,
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

            if (_ownedDirectory is not null && Directory.Exists(_ownedDirectory))
            {
                Directory.Delete(_ownedDirectory, recursive: true);
            }
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
            => siloBuilder.AddFileGrainStorage(
                "PubSubStore",
                options => options.RootDirectory =
                    siloBuilder.Configuration[PubSubStoreRootDirectoryKey]
                    ?? throw new InvalidOperationException(
                        $"Missing {PubSubStoreRootDirectoryKey} configuration."));
    }
}
#endif
