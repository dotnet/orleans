#if NET10_0
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;
using OrleansAWSUtils.Streams;
using SqsMessage = Amazon.SQS.Model.Message;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[Collection(SQSStreamProviderBuilderTestCollection.CollectionName)]
[TestSuite("BVT")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS"), TestCategory("BVT")]
public sealed class SQSAspireIntegrationTests
{
    private const string ServiceKey = "orleans-sqs";
    private const string ServiceId = "aspire-sqs-service";
    private const string RegionAdapterKey = "aspire-region-adapter";

    [Fact]
    public async Task AspireAppModel_Region_ProducesWorkingSiloConfiguration()
    {
        await using var app = await CreateRegionAppAsync();
        var environment = await app.GetSiloEnvironmentAsync();
        var configuration = SqsAspireTestApp.NormalizeConfiguration(environment);
        var provider = GetProviderConfiguration(configuration, app.ProviderName);

        AssertExactConfiguration(
            new Dictionary<string, string?>
            {
                ["ProviderType"] = "SQS",
                ["PartitionCount"] = "4",
                ["FifoQueue"] = "False",
                ["ReceiveWaitTimeSeconds"] = "12",
                ["VisibilityTimeoutSeconds"] = "45",
                ["ReceiveMessageAttributes:0"] = "TraceId",
                ["ReceiveMessageAttributes:1"] = "Tenant",
                ["ReceiveMessageSystemAttributes:0"] = "SentTimestamp",
                ["ReceiveMessageSystemAttributes:1"] = "SequenceNumber",
                ["DataAdapterKey"] = RegionAdapterKey,
                ["CacheSize"] = "2048",
            },
            provider);
        Assert.Equal("integration-profile", environment["AWS_PROFILE"]);
        Assert.Equal("us-east-1", environment["AWS_REGION"]);
        Assert.DoesNotContain(configuration.Keys, key => key.StartsWith("ConnectionStrings:", StringComparison.Ordinal));
        AssertSecretFree(environment);
        Assert.All(
            app.Model.Resources,
            resource => Assert.Contains(
                resource.Annotations,
                annotation => annotation.GetType().Name == "SDKResourceAnnotation"));
    }

    [Fact]
    public async Task AspireAppModel_CustomEndpoint_ProducesWorkingClientConfiguration()
    {
        await using var app = await CreateEndpointAppAsync();
        var environment = await app.GetClientEnvironmentAsync();
        var configuration = SqsAspireTestApp.NormalizeConfiguration(environment);
        var provider = GetProviderConfiguration(configuration, app.ProviderName);

        AssertExactConfiguration(
            new Dictionary<string, string?>
            {
                ["ProviderType"] = "SQS",
                ["ServiceKey"] = ServiceKey,
                ["PartitionCount"] = "3",
                ["FifoQueue"] = "True",
                ["ReceiveWaitTimeSeconds"] = "7",
                ["VisibilityTimeoutSeconds"] = "90",
                ["ReceiveMessageAttributes:0"] = "CorrelationId",
                ["ReceiveMessageSystemAttributes:0"] = "ApproximateReceiveCount",
            },
            provider);
        Assert.Equal(
            "Service=http://127.0.0.1:9324",
            configuration[$"ConnectionStrings:{ServiceKey}"]);
        AssertSecretFree(environment);
        Assert.Single(app.Model.Resources, resource => resource.Name == ServiceKey);
        Assert.DoesNotContain(
            app.Model.Resources,
            resource => resource.Name.Contains("queue", StringComparison.OrdinalIgnoreCase)
                || resource.GetType().Name.Contains("queue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_ActivatesSqsProviderOnSilo()
    {
        await using var app = await CreateRegionAppAsync();
        using var host = await app.BuildSiloHostAsync(services =>
            services.AddKeyedSingleton<ISQSDataAdapter>(
                RegionAdapterKey,
                new FakeSqsDataAdapter(RegionAdapterKey)));
        var sqsOptions = GetOptions<SqsOptions>(host.Services, app.ProviderName);
        var partitionOptions = GetOptions<HashRingStreamQueueMapperOptions>(host.Services, app.ProviderName);
        var cacheOptions = GetOptions<SimpleQueueCacheOptions>(host.Services, app.ProviderName);
        var adapterFactory = host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(app.ProviderName);
        var streamProvider = host.Services.GetRequiredKeyedService<IStreamProvider>(app.ProviderName);
        var configuredAdapter = host.Services.GetRequiredKeyedService<ISQSDataAdapter>(app.ProviderName);

        Assert.Equal("Service=us-east-1", sqsOptions.ConnectionString);
        Assert.False(sqsOptions.FifoQueue);
        Assert.Equal(4, partitionOptions.TotalQueueCount);
        Assert.Equal(2048, cacheOptions.CacheSize);
        Assert.IsType<SQSAdapterFactory>(adapterFactory);
        Assert.Equal(app.ProviderName, streamProvider.Name);
        Assert.Equal(RegionAdapterKey, Assert.IsType<FakeSqsDataAdapter>(configuredAdapter).Id);
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_ActivatesSqsProviderOnClient()
    {
        await using var app = await CreateEndpointAppAsync();
        using var host = await app.BuildClientHostAsync();
        var sqsOptions = GetOptions<SqsOptions>(host.Services, app.ProviderName);
        var partitionOptions = GetOptions<HashRingStreamQueueMapperOptions>(host.Services, app.ProviderName);
        var cacheOptions = GetOptions<SimpleQueueCacheOptions>(host.Services, app.ProviderName);
        var adapterFactory = host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(app.ProviderName);
        var streamProvider = host.Services.GetRequiredKeyedService<IStreamProvider>(app.ProviderName);

        Assert.Equal("Service=http://127.0.0.1:9324", sqsOptions.ConnectionString);
        Assert.True(sqsOptions.FifoQueue);
        Assert.Equal(3, partitionOptions.TotalQueueCount);
        Assert.Equal(SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE, cacheOptions.CacheSize);
        Assert.IsType<SQSAdapterFactory>(adapterFactory);
        Assert.Equal(app.ProviderName, streamProvider.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AspireGeneratedConfiguration_PreservesExplicitPartitionTopology(bool fifoQueue)
    {
        const string providerName = "Topology";
        await using var app = await SqsAspireTestApp.CreateAsync(
            providerName,
            [
                ("PartitionCount", "4"),
                ("FifoQueue", fifoQueue.ToString()),
            ],
            serviceId: ServiceId,
            awsRegion: "eu-central-1");
        using var siloHost = await app.BuildSiloHostAsync();
        using var clientHost = await app.BuildClientHostAsync();
        var siloSqsOptions = GetOptions<SqsOptions>(siloHost.Services, providerName);
        var clientSqsOptions = GetOptions<SqsOptions>(clientHost.Services, providerName);
        var siloMapper = CreateQueueMapper(siloHost.Services, providerName);
        var clientMapper = CreateQueueMapper(clientHost.Services, providerName);
        var siloQueueIds = siloMapper.GetAllQueues().Order().ToArray();
        var clientQueueIds = clientMapper.GetAllQueues().Order().ToArray();
        var siloPhysicalNames = GetPhysicalQueueNames(siloQueueIds, siloSqsOptions, ServiceId);
        var clientPhysicalNames = GetPhysicalQueueNames(clientQueueIds, clientSqsOptions, ServiceId);
        var expectedPhysicalNames = Enumerable.Range(0, 4)
            .Select(index => $"{ServiceId}-topology-{index}{(fifoQueue ? ".fifo" : string.Empty)}")
            .Order()
            .ToArray();

        Assert.Equal(4, siloQueueIds.Length);
        Assert.Equal(siloQueueIds, clientQueueIds);
        Assert.Equal(expectedPhysicalNames, siloPhysicalNames);
        Assert.Equal(siloPhysicalNames, clientPhysicalNames);
        Assert.All(
            siloPhysicalNames,
            name => Assert.Equal(fifoQueue, name.EndsWith(".fifo", StringComparison.Ordinal)));
    }

    private static Task<SqsAspireTestApp> CreateRegionAppAsync()
        => SqsAspireTestApp.CreateAsync(
            "orders-stream",
            [
                ("PartitionCount", "4"),
                ("CacheSize", "2048"),
                ("FifoQueue", "False"),
                ("ReceiveWaitTimeSeconds", "12"),
                ("VisibilityTimeoutSeconds", "45"),
                ("ReceiveMessageAttributes:0", "TraceId"),
                ("ReceiveMessageAttributes:1", "Tenant"),
                ("ReceiveMessageSystemAttributes:0", "SentTimestamp"),
                ("ReceiveMessageSystemAttributes:1", "SequenceNumber"),
                ("DataAdapterKey", RegionAdapterKey),
            ],
            serviceId: ServiceId,
            awsProfile: "integration-profile",
            awsRegion: "us-east-1");

    private static Task<SqsAspireTestApp> CreateEndpointAppAsync()
        => SqsAspireTestApp.CreateAsync(
            "critical-orders",
            [
                ("ServiceKey", ServiceKey),
                ("PartitionCount", "3"),
                ("FifoQueue", "True"),
                ("ReceiveWaitTimeSeconds", "7"),
                ("VisibilityTimeoutSeconds", "90"),
                ("ReceiveMessageAttributes:0", "CorrelationId"),
                ("ReceiveMessageSystemAttributes:0", "ApproximateReceiveCount"),
            ],
            [($"ConnectionStrings:{ServiceKey}", "Service=http://127.0.0.1:9324")],
            ServiceId);

    private static IReadOnlyDictionary<string, string?> GetProviderConfiguration(
        IReadOnlyDictionary<string, string?> configuration,
        string providerName)
    {
        var prefix = $"Orleans:Streaming:{providerName}:";
        return configuration
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                pair => pair.Value,
                StringComparer.Ordinal);
    }

    private static void AssertExactConfiguration(
        IReadOnlyDictionary<string, string?> expected,
        IReadOnlyDictionary<string, string?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, expectedValue) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var actualValue), $"Missing generated key '{key}'.");
            Assert.Equal(expectedValue, actualValue);
        }
    }

    private static void AssertSecretFree(IReadOnlyDictionary<string, string?> environment)
    {
        string[] forbiddenFragments = ["ACCESS_KEY", "SECRET", "SESSION_TOKEN", "QUEUE_URL"];
        Assert.DoesNotContain(
            environment,
            pair => forbiddenFragments.Any(
                fragment => pair.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                    || pair.Value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static TOptions GetOptions<TOptions>(IServiceProvider services, string providerName)
        where TOptions : class
        => services.GetRequiredService<IOptionsMonitor<TOptions>>().Get(providerName);

    private static HashRingBasedStreamQueueMapper CreateQueueMapper(IServiceProvider services, string providerName)
        => new(GetOptions<HashRingStreamQueueMapperOptions>(services, providerName), providerName);

    private static string[] GetPhysicalQueueNames(
        IEnumerable<QueueId> queueIds,
        SqsOptions options,
        string serviceId)
    {
        var storageType = typeof(SqsStreamProviderBuilder).Assembly.GetType(
            "OrleansAWSUtils.Storage.SQSStorage",
            throwOnError: true)!;
        var constructQueueName = storageType.GetMethod(
            "ConstructQueueName",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return queueIds
            .Select(queueId => (string)constructQueueName.Invoke(null, [queueId.ToString(), options, serviceId])!)
            .Order()
            .ToArray();
    }

    private sealed class FakeSqsDataAdapter(string id) : ISQSDataAdapter
    {
        public string Id { get; } = id;

        public IBatchContainer FromQueueMessage(SqsMessage queueMessage, long sequenceId)
            => throw new NotSupportedException();

        public SqsMessage ToQueueMessage<T>(
            Orleans.Runtime.StreamId streamId,
            IEnumerable<T> events,
            StreamSequenceToken? token,
            Dictionary<string, object>? requestContext)
            => throw new NotSupportedException();
    }
}
#endif
