using System.Globalization;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
#if NET10_0
using Aspire.Hosting.Testing;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;
using OrleansAWSUtils.Streams;
using SqsMessage = Amazon.SQS.Model.Message;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[TestSuite("BVT")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS"), TestCategory("BVT")]
public sealed class SQSAspireIntegrationTests
{
    private const string ServiceKey = "orleans-sqs";
    private const string ServiceId = "aspire-sqs-service";
    private const string SiloResourceName = "silo";
    private const string ClientResourceName = "client";

    [Fact]
    public async Task AspireAppModel_Region_ProducesStandardOrleansStreamingConfiguration()
    {
        var generated = await GenerateRegionConfigurationAsync();
        var siloProvider = GetProviderConfiguration(generated.Silo, generated.ProviderName);
        var clientProvider = GetProviderConfiguration(generated.Client, generated.ProviderName);
        var expectedCommon = new Dictionary<string, string?>
        {
            ["ProviderType"] = "SQS",
            ["ServiceKey"] = ServiceKey,
            ["PartitionCount"] = "4",
            ["FifoQueue"] = "False",
            ["ReceiveWaitTimeSeconds"] = "12",
            ["VisibilityTimeoutSeconds"] = "45",
            ["ReceiveMessageAttributes:0"] = "TraceId",
            ["ReceiveMessageAttributes:1"] = "Tenant",
            ["ReceiveMessageSystemAttributes:0"] = "SentTimestamp",
            ["ReceiveMessageSystemAttributes:1"] = "SequenceNumber",
            ["DataAdapterKey"] = RegionAdapterKey,
        };

        AssertExactConfiguration(expectedCommon.Append(new("CacheSize", "2048")), siloProvider);
        AssertExactConfiguration(expectedCommon, clientProvider);
        Assert.Equal("Service=us-east-1", generated.Silo[$"ConnectionStrings:{ServiceKey}"]);
        Assert.Equal(generated.Silo[$"ConnectionStrings:{ServiceKey}"], generated.Client[$"ConnectionStrings:{ServiceKey}"]);
        Assert.Equal("integration-profile", generated.Silo["AWS_PROFILE"]);
        Assert.Equal("us-east-1", generated.Silo["AWS_REGION"]);
        Assert.Equal(generated.Silo["AWS_PROFILE"], generated.Client["AWS_PROFILE"]);
        Assert.Equal(generated.Silo["AWS_REGION"], generated.Client["AWS_REGION"]);
        AssertSecretFree(generated);
        AssertSharedServiceResource(generated);
    }

    [Fact]
    public async Task AspireAppModel_CustomEndpoint_ProducesFifoOrleansStreamingConfiguration()
    {
        var generated = await GenerateEndpointConfigurationAsync();
        var siloProvider = GetProviderConfiguration(generated.Silo, generated.ProviderName);
        var clientProvider = GetProviderConfiguration(generated.Client, generated.ProviderName);
        var expectedCommon = new Dictionary<string, string?>
        {
            ["ProviderType"] = "SQS",
            ["ServiceKey"] = ServiceKey,
            ["PartitionCount"] = "3",
            ["FifoQueue"] = "True",
            ["ReceiveWaitTimeSeconds"] = "7",
            ["VisibilityTimeoutSeconds"] = "90",
            ["ReceiveMessageAttributes:0"] = "CorrelationId",
            ["ReceiveMessageSystemAttributes:0"] = "ApproximateReceiveCount",
        };

        AssertExactConfiguration(expectedCommon.Append(new("CacheSize", "1024")), siloProvider);
        AssertExactConfiguration(expectedCommon, clientProvider);
        Assert.Equal("Service=http://127.0.0.1:9324", generated.Silo[$"ConnectionStrings:{ServiceKey}"]);
        Assert.Equal(generated.Silo[$"ConnectionStrings:{ServiceKey}"], generated.Client[$"ConnectionStrings:{ServiceKey}"]);

        using var host = CreateClientHost(generated.Client);
        var sqsOptions = GetOptions<SqsOptions>(host.Services, generated.ProviderName);
        var queueOptions = GetOptions<HashRingStreamQueueMapperOptions>(host.Services, generated.ProviderName);
        var mapper = new HashRingBasedStreamQueueMapper(queueOptions, generated.ProviderName);
        var physicalQueueNames = GetPhysicalQueueNames(
            mapper.GetAllQueues(),
            sqsOptions,
            ServiceId);
        var expectedQueueNames = Enumerable.Range(0, 3)
            .Select(index => $"{ServiceId}-{generated.ProviderName}-{index}.fifo")
            .Order()
            .ToArray();

        Assert.Equal(expectedQueueNames, physicalQueueNames);
        Assert.All(physicalQueueNames, name => Assert.EndsWith(".fifo", name, StringComparison.Ordinal));
        Assert.DoesNotContain(physicalQueueNames, name => generated.ResourceNames.Contains(name, StringComparer.Ordinal));
        AssertSharedServiceResource(generated);
        AssertSecretFree(generated);
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_ActivatesSqsProviderOnSilo()
    {
        var generated = await GenerateRegionConfigurationAsync();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(generated.Silo);
        hostBuilder.Services.AddKeyedSingleton<ISQSDataAdapter>(
            RegionAdapterKey,
            new FakeSqsDataAdapter(RegionAdapterKey));
        hostBuilder.UseOrleans();

        using var host = hostBuilder.Build();
        var sqsOptions = GetOptions<SqsOptions>(host.Services, generated.ProviderName);
        var partitionOptions = GetOptions<HashRingStreamQueueMapperOptions>(host.Services, generated.ProviderName);
        var cacheOptions = GetOptions<SimpleQueueCacheOptions>(host.Services, generated.ProviderName);
        var adapterFactory = host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(generated.ProviderName);
        var streamProvider = host.Services.GetRequiredKeyedService<IStreamProvider>(generated.ProviderName);
        var configuredAdapter = host.Services.GetRequiredKeyedService<ISQSDataAdapter>(generated.ProviderName);

        Assert.Equal("Service=us-east-1", sqsOptions.ConnectionString);
        Assert.False(sqsOptions.FifoQueue);
        Assert.Equal(4, partitionOptions.TotalQueueCount);
        Assert.Equal(2048, cacheOptions.CacheSize);
        Assert.IsType<SQSAdapterFactory>(adapterFactory);
        Assert.Equal(generated.ProviderName, streamProvider.Name);
        Assert.Equal(RegionAdapterKey, Assert.IsType<FakeSqsDataAdapter>(configuredAdapter).Id);
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_ActivatesSqsProviderOnClient()
    {
        var generated = await GenerateEndpointConfigurationAsync();

        using var host = CreateClientHost(generated.Client);
        var sqsOptions = GetOptions<SqsOptions>(host.Services, generated.ProviderName);
        var partitionOptions = GetOptions<HashRingStreamQueueMapperOptions>(host.Services, generated.ProviderName);
        var cacheOptions = GetOptions<SimpleQueueCacheOptions>(host.Services, generated.ProviderName);
        var adapterFactory = host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(generated.ProviderName);
        var streamProvider = host.Services.GetRequiredKeyedService<IStreamProvider>(generated.ProviderName);

        Assert.Equal("Service=http://127.0.0.1:9324", sqsOptions.ConnectionString);
        Assert.True(sqsOptions.FifoQueue);
        Assert.Equal(3, partitionOptions.TotalQueueCount);
        Assert.Equal(SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE, cacheOptions.CacheSize);
        Assert.IsType<SQSAdapterFactory>(adapterFactory);
        Assert.Equal(generated.ProviderName, streamProvider.Name);
        Assert.DoesNotContain(
            $"Orleans:Streaming:{generated.ProviderName}:CacheSize",
            generated.Client.Keys);
    }

    private const string RegionAdapterKey = "aspire-region-adapter";

    private static Task<GeneratedConfiguration> GenerateRegionConfigurationAsync()
        => GenerateConfigurationAsync(
            new SqsProviderSettings(
                ProviderName: "orders-stream",
                ServiceLocation: "us-east-1",
                PartitionCount: 4,
                CacheSize: 2048,
                FifoQueue: false,
                ReceiveWaitTimeSeconds: 12,
                VisibilityTimeoutSeconds: 45,
                ReceiveMessageAttributes: ["TraceId", "Tenant"],
                ReceiveMessageSystemAttributes: ["SentTimestamp", "SequenceNumber"],
                DataAdapterKey: RegionAdapterKey),
            new AwsSdkConfiguration("integration-profile", "us-east-1"));

    private static Task<GeneratedConfiguration> GenerateEndpointConfigurationAsync()
        => GenerateConfigurationAsync(
            new SqsProviderSettings(
                ProviderName: "critical-orders",
                ServiceLocation: "http://127.0.0.1:9324",
                PartitionCount: 3,
                CacheSize: 1024,
                FifoQueue: true,
                ReceiveWaitTimeSeconds: 7,
                VisibilityTimeoutSeconds: 90,
                ReceiveMessageAttributes: ["CorrelationId"],
                ReceiveMessageSystemAttributes: ["ApproximateReceiveCount"],
                DataAdapterKey: null),
            new AwsSdkConfiguration("elasticmq-profile", "us-west-2"));

    private static async Task<GeneratedConfiguration> GenerateConfigurationAsync(
        SqsProviderSettings settings,
        AwsSdkConfiguration aws)
    {
#if NET10_0
        await using var builder = DistributedApplicationTestingBuilder.Create();
#else
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                Args = [],
                DisableDashboard = true,
            });
#endif
        var connection = builder.AddConnectionString(
            ServiceKey,
            ReferenceExpression.Create($"Service={settings.ServiceLocation}"));
        var provider = new SqsProviderConfiguration(connection, aws, settings);
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(new TestClusteringConfiguration())
            .WithServiceId(ServiceId)
            .WithStreaming(settings.ProviderName, provider);
        var silo = builder.AddContainer(SiloResourceName, "unused")
            .WithReference(orleans);
        var client = builder.AddContainer(ClientResourceName, "unused")
            .WithReference(orleans.AsClient());

#if NET10_0
        await using var app = await builder.BuildAsync();
#else
        using var app = builder.Build();
#endif
        var siloEnvironment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);
        var clientEnvironment = await GetEnvironmentVariablesAsync(client.Resource, app.Services);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        return new GeneratedConfiguration(
            settings.ProviderName,
            siloEnvironment,
            clientEnvironment,
            model.Resources.Select(resource => resource.Name).ToArray(),
            model.Resources.Select(resource => resource.GetType().FullName ?? resource.GetType().Name).ToArray());
    }

    private static IHost CreateClientHost(IReadOnlyDictionary<string, string?> configuration)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(configuration);
        hostBuilder.UseOrleansClient();
        return hostBuilder.Build();
    }

    private static async Task<Dictionary<string, string?>> GetEnvironmentVariablesAsync(
        IResource resource,
        IServiceProvider services)
    {
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

            var normalizedKey = key.StartsWith("Orleans__", StringComparison.Ordinal)
                || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
                    ? key.Replace("__", ":", StringComparison.Ordinal)
                    : key;
            if (value is not IValueProvider provider)
            {
                result[normalizedKey] = value.ToString();
                continue;
            }

            try
            {
                result[normalizedKey] = await provider
                    .GetValueAsync(valueContext)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10));
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
            || name.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
            || name.StartsWith("AWS_", StringComparison.Ordinal);

    private static Dictionary<string, string?> GetProviderConfiguration(
        IReadOnlyDictionary<string, string?> environment,
        string providerName)
    {
        var prefix = $"Orleans:Streaming:{providerName}:";
        return environment
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                pair => pair.Value,
                StringComparer.Ordinal);
    }

    private static void AssertExactConfiguration(
        IEnumerable<KeyValuePair<string, string?>> expectedValues,
        IReadOnlyDictionary<string, string?> actual)
    {
        var expected = expectedValues.ToDictionary(StringComparer.Ordinal);
        Assert.True(
            expected.Count == actual.Count,
            $"Expected {expected.Count} provider values, found {actual.Count}: {string.Join(", ", actual.Keys.Order())}");
        foreach (var (key, expectedValue) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var actualValue), $"Missing generated key '{key}'.");
            Assert.Equal(expectedValue, actualValue);
        }
    }

    private static void AssertSharedServiceResource(GeneratedConfiguration generated)
    {
        Assert.Equal(1, generated.ResourceNames.Count(name => name == ServiceKey));
        Assert.DoesNotContain(
            generated.ResourceNames.Zip(generated.ResourceTypeNames),
            resource => resource.First.Contains("queue", StringComparison.OrdinalIgnoreCase)
                || resource.Second.Contains("queue", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSecretFree(GeneratedConfiguration generated)
    {
        string[] forbiddenFragments =
        [
            "ACCESS_KEY",
            "SECRET",
            "SESSION_TOKEN",
            "QUEUE_URL",
        ];
        var generatedInputs = generated.Silo.Concat(generated.Client).ToArray();

        Assert.DoesNotContain(
            generatedInputs,
            pair => forbiddenFragments.Any(
                fragment => pair.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            generatedInputs,
            pair => pair.Value is not null
                && forbiddenFragments.Any(
                    fragment => pair.Value.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static TOptions GetOptions<TOptions>(IServiceProvider services, string providerName)
        where TOptions : class
        => services.GetRequiredService<IOptionsMonitor<TOptions>>().Get(providerName);

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

    private sealed class SqsProviderConfiguration(
        IResourceBuilder<IResourceWithConnectionString> connection,
        AwsSdkConfiguration aws,
        SqsProviderSettings settings) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder
                .WithReference(connection)
                .WithEnvironment($"{prefix}__ProviderType", "SQS")
                .WithEnvironment($"{prefix}__ServiceKey", ServiceKey)
                .WithEnvironment(
                    $"{prefix}__PartitionCount",
                    settings.PartitionCount.ToString(CultureInfo.InvariantCulture))
                .WithEnvironment($"{prefix}__FifoQueue", settings.FifoQueue.ToString())
                .WithEnvironment(
                    $"{prefix}__ReceiveWaitTimeSeconds",
                    settings.ReceiveWaitTimeSeconds.ToString(CultureInfo.InvariantCulture))
                .WithEnvironment(
                    $"{prefix}__VisibilityTimeoutSeconds",
                    settings.VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            aws.ConfigureResource(resourceBuilder);

            for (var index = 0; index < settings.ReceiveMessageAttributes.Length; index++)
            {
                resourceBuilder.WithEnvironment(
                    $"{prefix}__ReceiveMessageAttributes__{index}",
                    settings.ReceiveMessageAttributes[index]);
            }

            for (var index = 0; index < settings.ReceiveMessageSystemAttributes.Length; index++)
            {
                resourceBuilder.WithEnvironment(
                    $"{prefix}__ReceiveMessageSystemAttributes__{index}",
                    settings.ReceiveMessageSystemAttributes[index]);
            }

            if (settings.DataAdapterKey is { } dataAdapterKey)
            {
                resourceBuilder.WithEnvironment($"{prefix}__DataAdapterKey", dataAdapterKey);
            }

            if (resourceBuilder.Resource.Name == SiloResourceName)
            {
                resourceBuilder.WithEnvironment(
                    $"{prefix}__CacheSize",
                    settings.CacheSize.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private sealed class AwsSdkConfiguration(string profile, string region)
    {
        public void ConfigureResource<T>(IResourceBuilder<T> resourceBuilder)
            where T : IResourceWithEnvironment
            => resourceBuilder
                .WithEnvironment("AWS_PROFILE", profile)
                .WithEnvironment("AWS_REGION", region);
    }

    private sealed class TestClusteringConfiguration : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder.WithEnvironment($"{prefix}__ProviderType", "Development");
            if (resourceBuilder.Resource.Name == SiloResourceName)
            {
                resourceBuilder.WithEnvironment(
                    $"{prefix}__PrimarySiloEndPoint",
                    "127.0.0.1:11111");
            }
            else
            {
                resourceBuilder.WithEnvironment(
                    $"{prefix}__Gateways__0",
                    "gwy.tcp://127.0.0.1:30000/0");
            }
        }
    }

    private sealed record SqsProviderSettings(
        string ProviderName,
        string ServiceLocation,
        int PartitionCount,
        int CacheSize,
        bool FifoQueue,
        int ReceiveWaitTimeSeconds,
        int VisibilityTimeoutSeconds,
        string[] ReceiveMessageAttributes,
        string[] ReceiveMessageSystemAttributes,
        string? DataAdapterKey);

    private sealed record GeneratedConfiguration(
        string ProviderName,
        Dictionary<string, string?> Silo,
        Dictionary<string, string?> Client,
        string[] ResourceNames,
        string[] ResourceTypeNames);

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
