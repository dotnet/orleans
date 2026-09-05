using Amazon;
using Amazon.CDK.AWS.SQS;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AWS.CDK;
using Aspire.Hosting.Orleans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;
using OrleansAWSUtils.Storage;
using TestExtensions;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[Collection(SQSStreamProviderBuilderTestCollection.CollectionName)]
[TestSuite("BVT")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS"), TestCategory("BVT")]
public sealed class SqsStreamingResourceTests
{
    private const string ProviderName = "Orders";
    private const string ServiceId = "orders-service";

    [Fact]
    public async Task AddSqsStreaming_StandardTopology_ConfiguresCdkEnvironmentAndWaits()
    {
        await using var app = await CreateAppAsync(
            new SqsStreamingOptions
            {
                ServiceId = ServiceId,
                PartitionCount = 3,
                ReceiveWaitTimeSeconds = 12,
                VisibilityTimeoutSeconds = 45,
                CacheSize = 2048,
                DataAdapterKey = "orders-adapter",
                ReceiveMessageAttributes = ["TraceId", "Tenant"],
                ReceiveMessageSystemAttributes = ["SentTimestamp"],
            });

        Assert.Equal(ProviderName, app.Streaming.Name);
        Assert.Equal(ServiceId, app.Streaming.Options.ServiceId);
        Assert.Equal("us-east-1", app.Streaming.AwsSdkConfig.Region?.SystemName);
        Assert.Equal(3, app.Streaming.Queues.Count);
        Assert.Equal("cluster-orders-sqs", app.Streaming.Stack.Resource.Name);

        var queues = GetQueues(app.Streaming);
        Assert.Equal(
            ["orders-service-orders-0", "orders-service-orders-1", "orders-service-orders-2"],
            queues.Select(queue => queue.QueueName));
        Assert.All(queues, queue =>
        {
            Assert.NotEqual(true, queue.FifoQueue);
            Assert.Equal(12, queue.ReceiveMessageWaitTimeSeconds);
            Assert.Equal(45, queue.VisibilityTimeout);
        });

        var siloEnvironment = await app.GetSiloEnvironmentAsync();
        var clientEnvironment = await app.GetClientEnvironmentAsync();
        AssertProviderEnvironment(siloEnvironment, fifoQueue: false);
        AssertProviderEnvironment(clientEnvironment, fifoQueue: false);
        AssertWaitsForStack(app.Silo, app.Streaming);
        AssertWaitsForStack(app.Client, app.Streaming);
    }

    [Fact]
    public async Task AddSqsStreaming_FifoTopology_UsesRuntimeQueueNamesAndFifoProperties()
    {
        await using var app = await CreateAppAsync(
            new SqsStreamingOptions
            {
                ServiceId = ServiceId,
                PartitionCount = 4,
                FifoQueue = true,
            });

        var expectedNames = new HashRingBasedStreamQueueMapper(
                new HashRingStreamQueueMapperOptions { TotalQueueCount = 4 },
                ProviderName)
            .GetAllQueues()
            .Select(queue => SqsQueueName.Create(queue.ToString(), fifoQueue: true, ServiceId))
            .Order()
            .ToArray();
        var queues = GetQueues(app.Streaming);

        Assert.Equal(expectedNames, queues.Select(queue => queue.QueueName).Order());
        Assert.All(queues, queue =>
        {
            Assert.Equal(true, queue.FifoQueue);
            Assert.Equal(true, queue.ContentBasedDeduplication);
            Assert.Equal("messageGroup", queue.DeduplicationScope);
            Assert.Equal("perMessageGroupId", queue.FifoThroughputLimit);
        });
    }

    [Fact]
    public async Task WithSqsStreaming_ReturnsOrleansServiceAndActivatesSiloAndClientProviders()
    {
        _ = typeof(SqsStreamProviderBuilder);
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig()
            .WithProfile("integration-profile")
            .WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster")
            .WithDevelopmentClustering()
            .WithSqsStreaming(
                ProviderName,
                aws,
                new SqsStreamingOptions
                {
                    ServiceId = ServiceId,
                    PartitionCount = 2,
                    FifoQueue = true,
                });
        var silo = builder.AddContainer("silo", "unused").WithReference(orleans);
        var client = builder.AddContainer("client", "unused").WithReference(orleans.AsClient());
        await using var application = builder.Build();

        using (new EnvironmentVariableScope(
            await GetEnvironmentAsync(application.Services, silo.Resource)))
        {
            using var siloHost = Host.CreateApplicationBuilder().UseOrleans().Build();
            AssertActivatedProvider(siloHost.Services, partitionCount: 2, fifoQueue: true);
        }

        using (new EnvironmentVariableScope(
            await GetEnvironmentAsync(application.Services, client.Resource)))
        {
            using var clientHost = Host.CreateApplicationBuilder().UseOrleansClient().Build();
            AssertActivatedProvider(clientHost.Services, partitionCount: 2, fifoQueue: true);
        }
    }

    [Theory]
    [InlineData("", 1, null, null, "ServiceId")]
    [InlineData("service", 0, null, null, "PartitionCount")]
    [InlineData("service", 1, -1, null, "ReceiveWaitTimeSeconds")]
    [InlineData("service", 1, 21, null, "ReceiveWaitTimeSeconds")]
    [InlineData("service", 1, null, 43_201, "VisibilityTimeoutSeconds")]
    public void AddSqsStreaming_InvalidOptions_ThrowsActionableError(
        string serviceId,
        int partitionCount,
        int? receiveWaitTimeSeconds,
        int? visibilityTimeoutSeconds,
        string expectedMessage)
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var options = new SqsStreamingOptions
        {
            ServiceId = serviceId,
            PartitionCount = partitionCount,
            ReceiveWaitTimeSeconds = receiveWaitTimeSeconds,
            VisibilityTimeoutSeconds = visibilityTimeoutSeconds,
        };

        var exception = Assert.ThrowsAny<ArgumentException>(
            () => orleans.AddSqsStreaming(ProviderName, aws, options));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSqsStreaming_MissingAwsRegion_ThrowsActionableError()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig();
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();

        var exception = Assert.Throws<ArgumentException>(
            () => orleans.AddSqsStreaming(
                ProviderName,
                aws,
                new SqsStreamingOptions { ServiceId = ServiceId }));

        Assert.Contains("concrete AWS region", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSqsStreaming_NullArguments_ThrowWithParameterNames()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var options = new SqsStreamingOptions { ServiceId = ServiceId };

        Assert.Equal(
            "orleansService",
            Assert.Throws<ArgumentNullException>(
                () => OrleansSqsStreamingExtensions.AddSqsStreaming(null!, ProviderName, aws, options)).ParamName);
        Assert.Equal(
            "awsSdkConfig",
            Assert.Throws<ArgumentNullException>(
                () => orleans.AddSqsStreaming(ProviderName, null!, options)).ParamName);
        Assert.Equal(
            "options",
            Assert.Throws<ArgumentNullException>(
                () => orleans.AddSqsStreaming(ProviderName, aws, null!)).ParamName);
        Assert.Equal(
            "name",
            Assert.Throws<ArgumentException>(
                () => orleans.AddSqsStreaming(" ", aws, options)).ParamName);
    }

    [Fact]
    public void AddSqsStreaming_InvalidCacheSize_ThrowsActionableError()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => orleans.AddSqsStreaming(
                ProviderName,
                aws,
                new SqsStreamingOptions
                {
                    ServiceId = ServiceId,
                    CacheSize = 0,
                }));

        Assert.Contains("CacheSize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSqsStreaming_DataAdapterKeyMatchingProviderName_ThrowsActionableError()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();

        var exception = Assert.Throws<ArgumentException>(
            () => orleans.AddSqsStreaming(
                ProviderName,
                aws,
                new SqsStreamingOptions
                {
                    ServiceId = ServiceId,
                    DataAdapterKey = ProviderName,
                }));

        Assert.Contains("must differ", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddSqsStreaming_NullAttributeList_ThrowsWithPropertyName(bool messageAttributes)
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var options = messageAttributes
            ? new SqsStreamingOptions
            {
                ServiceId = ServiceId,
                ReceiveMessageAttributes = null!,
            }
            : new SqsStreamingOptions
            {
                ServiceId = ServiceId,
                ReceiveMessageSystemAttributes = null!,
            };

        var exception = Assert.Throws<ArgumentNullException>(
            () => orleans.AddSqsStreaming(ProviderName, aws, options));

        Assert.Equal(
            messageAttributes ? "ReceiveMessageAttributes" : "ReceiveMessageSystemAttributes",
            exception.ParamName);
    }

    [Fact]
    public void AddSqsStreaming_InvalidGeneratedQueueName_ThrowsActionableError()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();

        var exception = Assert.Throws<ArgumentException>(
            () => orleans.AddSqsStreaming(
                "Orders.With.Invalid.Characters",
                aws,
                new SqsStreamingOptions { ServiceId = ServiceId }));

        Assert.Contains("generated SQS queue name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSqsStreaming_OverlongGeneratedQueueName_ThrowsActionableError()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();

        var exception = Assert.Throws<ArgumentException>(
            () => orleans.AddSqsStreaming(
                ProviderName,
                aws,
                new SqsStreamingOptions { ServiceId = new string('s', 75) }));

        Assert.Contains("at most 80 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSqsStreaming_NonalphanumericProviderName_UsesStableConstructIds()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var resource = orleans.AddSqsStreaming(
            "-",
            aws,
            new SqsStreamingOptions
            {
                ServiceId = ServiceId,
                PartitionCount = 2,
            });

        Assert.Equal(["sqs-0", "sqs-1"], resource.Queues.Select(queue => queue.Resource.Name));
        Assert.Equal(
            ["orders-service---0", "orders-service---1"],
            GetQueues(resource).Select(queue => queue.QueueName));
    }

    [Fact]
    public void AddSqsStreaming_ConflictingServiceIdsAcrossProviders_ThrowsActionableError()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        orleans.AddSqsStreaming(
            "Orders",
            aws,
            new SqsStreamingOptions { ServiceId = "orders-service" });

        var exception = Assert.Throws<InvalidOperationException>(
            () => orleans.AddSqsStreaming(
                "Billing",
                aws,
                new SqsStreamingOptions { ServiceId = "billing-service" }));

        Assert.Contains("conflicts with Orleans ServiceId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders-service", exception.Message, StringComparison.Ordinal);
        Assert.Contains("billing-service", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithServiceId_AfterAddSqsStreaming_ConflictingValueFailsBeforeDeployment()
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        orleans.AddSqsStreaming(
            ProviderName,
            aws,
            new SqsStreamingOptions { ServiceId = ServiceId });
        orleans.WithServiceId("different-service");
        var silo = builder.AddContainer("silo", "unused").WithReference(orleans);
        await using var application = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetEnvironmentAsync(application.Services, silo.Resource));

        Assert.Contains(ServiceId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("different-service", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddSqsStreaming_DefensivelyCopiesAttributeLists()
    {
        var messageAttributes = new[] { "TraceId" };
        var systemAttributes = new[] { "SentTimestamp" };
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig().WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var resource = orleans.AddSqsStreaming(
            ProviderName,
            aws,
            new SqsStreamingOptions
            {
                ServiceId = ServiceId,
                ReceiveMessageAttributes = messageAttributes,
                ReceiveMessageSystemAttributes = systemAttributes,
            });

        messageAttributes[0] = "Changed";
        systemAttributes[0] = "Changed";

        Assert.Equal("TraceId", Assert.Single(resource.Options.ReceiveMessageAttributes));
        Assert.Equal("SentTimestamp", Assert.Single(resource.Options.ReceiveMessageSystemAttributes));
    }

    [Fact]
    public void EnvironmentVariableScope_ClearsAndRestoresInheritedAwsConfiguration()
    {
        var originalDefaultRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        var originalStructuredRegion = Environment.GetEnvironmentVariable("AWS__Region");
        try
        {
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", "us-west-2");
            Environment.SetEnvironmentVariable("AWS__Region", "us-west-2");

            using (new EnvironmentVariableScope(
                new Dictionary<string, string?> { ["AWS_REGION"] = "us-east-1" }))
            {
                Assert.Equal("us-east-1", Environment.GetEnvironmentVariable("AWS_REGION"));
                Assert.Null(Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"));
                Assert.Null(Environment.GetEnvironmentVariable("AWS__Region"));
            }

            Assert.Equal("us-west-2", Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"));
            Assert.Equal("us-west-2", Environment.GetEnvironmentVariable("AWS__Region"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", originalDefaultRegion);
            Environment.SetEnvironmentVariable("AWS__Region", originalStructuredRegion);
        }
    }

    private static async Task<TestApp> CreateAppAsync(SqsStreamingOptions options)
    {
        var builder = CreateBuilder();
        var aws = builder.AddAWSSDKConfig()
            .WithProfile("integration-profile")
            .WithRegion(RegionEndpoint.USEast1);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var streaming = orleans.AddSqsStreaming(ProviderName, aws, options);
        var silo = builder.AddContainer("silo", "unused").WithReference(orleans);
        var client = builder.AddContainer("client", "unused").WithReference(orleans.AsClient());
        var application = builder.Build();
        return new TestApp(application, streaming, silo.Resource, client.Resource);
    }

    private static IDistributedApplicationBuilder CreateBuilder()
        => DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                Args = [],
                DisableDashboard = true,
            });

    private static CfnQueue[] GetQueues(SqsStreamingResource streaming)
        => streaming.Queues
            .Select(queue => Assert.IsType<CfnQueue>(queue.Resource.Construct.Node.DefaultChild))
            .OrderBy(queue => queue.QueueName)
            .ToArray();

    private static async Task<IReadOnlyDictionary<string, string?>> GetEnvironmentAsync(
        IServiceProvider services,
        IResource resource)
    {
        Assert.IsAssignableFrom<IResourceWithEnvironment>(resource);
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
            {
                ServiceProvider = services,
            });
        var values = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(executionContext, resource, values);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(callbackContext).WaitAsync(TimeSpan.FromSeconds(10));
        }

        var valueContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
            Network = KnownNetworkIdentifiers.LocalhostNetwork,
        };
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!IsRelevantEnvironmentVariable(key))
            {
                continue;
            }

            try
            {
                result[key] = value switch
                {
                    IValueProvider provider => await provider
                        .GetValueAsync(valueContext)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(10)),
                    _ => value.ToString(),
                };
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out resolving environment variable '{key}' for resource '{resource.Name}'.",
                    exception);
            }
        }
        return result;
    }

    private static bool IsRelevantEnvironmentVariable(string name)
        => name.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || name is "Orleans__ClusterId" or "Orleans__ServiceId"
            || name.StartsWith("AWS_", StringComparison.Ordinal);

    private static void AssertProviderEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        bool fifoQueue)
    {
        const string prefix = "Orleans__Streaming__Orders__";
        Assert.Equal(ServiceId, environment["Orleans__ServiceId"]);
        Assert.Equal("integration-profile", environment["AWS_PROFILE"]);
        Assert.Equal("us-east-1", environment["AWS_REGION"]);
        Assert.Equal("SQS", environment[$"{prefix}ProviderType"]);
        Assert.Equal("us-east-1", environment[$"{prefix}Region"]);
        Assert.Equal("3", environment[$"{prefix}PartitionCount"]);
        Assert.Equal(fifoQueue.ToString(), environment[$"{prefix}FifoQueue"]);
        Assert.Equal("12", environment[$"{prefix}ReceiveWaitTimeSeconds"]);
        Assert.Equal("45", environment[$"{prefix}VisibilityTimeoutSeconds"]);
        Assert.Equal("2048", environment[$"{prefix}CacheSize"]);
        Assert.Equal("orders-adapter", environment[$"{prefix}DataAdapterKey"]);
        Assert.Equal("TraceId", environment[$"{prefix}ReceiveMessageAttributes__0"]);
        Assert.Equal("Tenant", environment[$"{prefix}ReceiveMessageAttributes__1"]);
        Assert.Equal("SentTimestamp", environment[$"{prefix}ReceiveMessageSystemAttributes__0"]);
    }

    private static void AssertWaitsForStack(IResource resource, SqsStreamingResource streaming)
        => Assert.Contains(
            resource.Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, streaming.Stack.Resource));

    private static void AssertActivatedProvider(
        IServiceProvider services,
        int partitionCount,
        bool fifoQueue)
    {
        var sqsOptions = services.GetRequiredService<IOptionsMonitor<SqsOptions>>().Get(ProviderName);
        var partitionOptions = services
            .GetRequiredService<IOptionsMonitor<HashRingStreamQueueMapperOptions>>()
            .Get(ProviderName);

        Assert.Equal("Service=us-east-1", sqsOptions.ConnectionString);
        Assert.Equal(fifoQueue, sqsOptions.FifoQueue);
        Assert.Equal(partitionCount, partitionOptions.TotalQueueCount);
    }

    private sealed class TestApp(
        DistributedApplication application,
        SqsStreamingResource streaming,
        IResource silo,
        IResource client) : IAsyncDisposable
    {
        public SqsStreamingResource Streaming { get; } = streaming;

        public IResource Silo { get; } = silo;

        public IResource Client { get; } = client;

        public Task<IReadOnlyDictionary<string, string?>> GetSiloEnvironmentAsync()
            => GetEnvironmentAsync(application.Services, Silo);

        public Task<IReadOnlyDictionary<string, string?>> GetClientEnvironmentAsync()
            => GetEnvironmentAsync(application.Services, Client);

        public ValueTask DisposeAsync() => application.DisposeAsync();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var key in new[]
            {
                "AWS_REGION",
                "AWS_DEFAULT_REGION",
                "AWS_PROFILE",
                "AWS__Region",
                "AWS__Profile",
            })
            {
                SaveAndSet(key, null);
            }

            foreach (var (key, value) in values)
            {
                SaveAndSet(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        private void SaveAndSet(string key, string? value)
        {
            _previousValues.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
