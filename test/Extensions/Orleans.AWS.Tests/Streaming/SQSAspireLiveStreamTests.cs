using AWSUtils.Tests.StorageTests;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.FileStorage;
using Orleans.Streaming.SQS;
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
    private Uri _sqsEndpoint = null!;

    protected override void CheckPreconditionsOrThrow()
    {
        if (!AWSTestConstants.IsSqsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("SQS connection string is not configured.");
        }

        var connection = SqsConnectionString.Parse(AWSTestConstants.SqsConnectionString);
        if (!connection.TryGetValue("Service", out var service)
            || !Uri.TryCreate(service, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.IsLoopback)
        {
            throw Xunit.Sdk.SkipException.ForSkip("The bridge live test requires a loopback HTTP SQS emulator.");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")))
        {
            throw Xunit.Sdk.SkipException.ForSkip("The bridge live test requires AWS SDK environment credentials for the SQS emulator.");
        }

        _sqsEndpoint = endpoint;
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
        _app = await SqsAspireTestApp.CreateBridgeAsync(
            ProviderName,
            new SqsStreamingOptions
            {
                ServiceId = $"aspire-sqs-{Guid.NewGuid():N}",
                PartitionCount = 4,
                ReceiveWaitTimeSeconds = 1,
                VisibilityTimeoutSeconds = 30,
            },
            _sqsEndpoint);
        _environment = await _app.CreateEnvironmentScopeAsync(
            SqsAspireResourceRole.Silo,
            streamingOnly: true);
        await base.InitializeAsync();
        var streaming = Assert.IsType<SqsStreamingResource>(_app.Streaming);
        var services = HostedCluster.GetSiloServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<SqsOptions>>().Get(ProviderName);
        Assert.Equal(streaming.Options.ServiceId, HostedCluster.Options.ServiceId);
        Assert.Equal("Service=us-east-1", options.ConnectionString);
        Assert.Equal(streaming.Options.ReceiveWaitTimeSeconds, options.ReceiveWaitTimeSeconds);
        Assert.Equal(streaming.Options.VisibilityTimeoutSeconds, options.VisibilityTimeoutSeconds);
        Assert.Equal(
            streaming.Queues.Count,
            services.GetRequiredService<IOptionsMonitor<HashRingStreamQueueMapperOptions>>()
                .Get(ProviderName).TotalQueueCount);
        _runner = new SingleStreamTestRunner(HostedCluster, ProviderName);
    }

    [Fact]
    public Task AspireBridgeConfiguration_PublishesConsumesAndAcknowledgesElasticMqStream()
    {
        return _runner.StreamTest_04_OneProducerClientOneConsumerClient(TestContext.Current.CancellationToken);
    }

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
                var services = HostedCluster.GetSiloServiceProvider();
                var partitionCount = services.GetRequiredService<IOptionsMonitor<HashRingStreamQueueMapperOptions>>()
                    .Get(ProviderName).TotalQueueCount;
                var sqsOptions = services.GetRequiredService<IOptionsMonitor<SqsOptions>>().Get(ProviderName);
                await base.DisposeAsync();
                await SQSStreamProviderUtils.DeleteAllUsedQueues(
                    ProviderName,
                    serviceId,
                    sqsOptions.ConnectionString,
                    NullLoggerFactory.Instance,
                    partitionCount,
                    sqsOptions.FifoQueue);
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
