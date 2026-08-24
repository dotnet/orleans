using System.Globalization;
#if NET10_0
using Amazon;
using Amazon.CDK.AWS.SQS;
#endif
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
#if NET10_0
using Aspire.Hosting.AWS;
using Aspire.Hosting.AWS.CDK;
#endif
using Aspire.Hosting.Orleans;
#if NET10_0
using Aspire.Hosting.Testing;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
#if NET10_0
using CdkDuration = Amazon.CDK.Duration;
#endif

namespace AWSUtils.Tests.Streaming;

internal sealed class SqsAspireTestApp : IAsyncDisposable
{
    private const string SiloResourceName = "silo";
    private const string ClientResourceName = "client";
    private readonly IAsyncDisposable? _builder;
    private readonly DistributedApplication _application;
    private readonly IResource _silo;
    private readonly IResource _client;

    private SqsAspireTestApp(
        IAsyncDisposable? builder,
        DistributedApplication application,
        IResource silo,
        IResource client,
        string providerName,
        string serviceId)
    {
        _builder = builder;
        _application = application;
        _silo = silo;
        _client = client;
        ProviderName = providerName;
        ServiceId = serviceId;
    }

    public string ProviderName { get; }

    public string ServiceId { get; }

    public DistributedApplicationModel Model
        => _application.Services.GetRequiredService<DistributedApplicationModel>();

    public static async Task<SqsAspireTestApp> CreateAsync(
        string providerName,
        IEnumerable<(string Key, string? Value)> providerValues,
        IEnumerable<(string Key, string? Value)>? rootValues = null,
        string serviceId = "aspire-sqs-service",
        string? awsProfile = null,
        string? awsRegion = null)
    {
#if NET10_0
        var builder = DistributedApplicationTestingBuilder.Create();
        IAsyncDisposable? builderLifetime = builder;
#else
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                Args = [],
                DisableDashboard = true,
            });
        IAsyncDisposable? builderLifetime = null;
#endif
        var providerValueArray = providerValues.ToArray();
        var connectionResources = new List<IResourceBuilder<IResourceWithConnectionString>>();
        var environmentValues = new List<(string Key, string? Value)>();
        foreach (var (key, value) in rootValues ?? [])
        {
            const string connectionStringPrefix = "ConnectionStrings:";
            if (key.StartsWith(connectionStringPrefix, StringComparison.OrdinalIgnoreCase))
            {
                connectionResources.Add(
                    builder.AddConnectionString(
                        key[connectionStringPrefix.Length..],
                        ReferenceExpression.Create($"{value}")));
            }
            else
            {
                environmentValues.Add((key, value));
            }
        }

        SqsAwsConfiguration? aws = null;
#if NET10_0
        if (awsProfile is not null || awsRegion is not null)
        {
            var awsSdkConfig = builder.AddAWSSDKConfig();
            if (awsProfile is not null)
            {
                awsSdkConfig.WithProfile(awsProfile);
            }

            if (awsRegion is not null)
            {
                awsSdkConfig.WithRegion(RegionEndpoint.GetBySystemName(awsRegion));
            }

            aws = new OfficialAwsConfiguration(awsSdkConfig);
            if (awsRegion is not null)
            {
                var stack = builder.AddAWSCDKStack($"{providerName.ToLowerInvariant()}-sqs")
                    .WithReference(awsSdkConfig);
                AddSqsQueues(stack, providerName, serviceId, providerValueArray);
            }
        }
#else
        if (awsProfile is not null || awsRegion is not null)
        {
            aws = new EnvironmentAwsConfiguration(awsProfile, awsRegion);
        }
#endif

        var provider = new SqsProviderConfiguration(
            aws,
            connectionResources,
            providerValueArray,
            environmentValues);
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(new TestClusteringConfiguration())
            .WithServiceId(serviceId)
            .WithStreaming(providerName, provider);
        var silo = builder.AddContainer(SiloResourceName, "unused")
            .WithReference(orleans);
        var client = builder.AddContainer(ClientResourceName, "unused")
            .WithReference(orleans.AsClient());
#if NET10_0
        var application = await builder.BuildAsync();
#else
        var application = builder.Build();
#endif

        return new SqsAspireTestApp(
            builderLifetime,
            application,
            silo.Resource,
            client.Resource,
            providerName,
            serviceId);
    }

    public Task<IReadOnlyDictionary<string, string?>> GetSiloEnvironmentAsync()
        => GetEnvironmentVariablesAsync(_silo);

    public Task<IReadOnlyDictionary<string, string?>> GetClientEnvironmentAsync()
        => GetEnvironmentVariablesAsync(_client);

    public async Task<EnvironmentVariableScope> CreateEnvironmentScopeAsync(
        SqsAspireResourceRole role,
        bool streamingOnly = false)
    {
        var resource = role == SqsAspireResourceRole.Silo ? _silo : _client;
        var values = await GetEnvironmentVariablesAsync(resource);
        if (streamingOnly)
        {
            var streamingPrefix = $"Orleans__Streaming__{ProviderName}__";
            values = values
                .Where(pair => pair.Key.StartsWith(streamingPrefix, StringComparison.Ordinal)
                    || pair.Key is "Orleans__ClusterId" or "Orleans__ServiceId"
                    || pair.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
                    || pair.Key.StartsWith("AWS_", StringComparison.Ordinal)
                    || pair.Key.StartsWith("AWS__", StringComparison.Ordinal))
                .ToDictionary(StringComparer.Ordinal);
        }

        return new EnvironmentVariableScope(values);
    }

    public async Task<IHost> BuildSiloHostAsync(Action<IServiceCollection>? configureServices = null)
    {
        using var environment = await CreateEnvironmentScopeAsync(SqsAspireResourceRole.Silo);
        var hostBuilder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(hostBuilder.Services);
        hostBuilder.UseOrleans();
        return hostBuilder.Build();
    }

    public async Task<IHost> BuildClientHostAsync(Action<IServiceCollection>? configureServices = null)
    {
        using var environment = await CreateEnvironmentScopeAsync(SqsAspireResourceRole.Client);
        var hostBuilder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(hostBuilder.Services);
        hostBuilder.UseOrleansClient();
        return hostBuilder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        await _application.DisposeAsync();
        if (_builder is not null)
        {
            await _builder.DisposeAsync();
        }
    }

    public static IReadOnlyDictionary<string, string?> NormalizeConfiguration(
        IReadOnlyDictionary<string, string?> environment)
        => environment.ToDictionary(
            pair => pair.Key.StartsWith("Orleans__", StringComparison.Ordinal)
                    || pair.Key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
                ? pair.Key.Replace("__", ":", StringComparison.Ordinal)
                : pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

    private async Task<IReadOnlyDictionary<string, string?>> GetEnvironmentVariablesAsync(IResource resource)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
            {
                ServiceProvider = _application.Services,
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
            || name.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
            || name.StartsWith("AWS_", StringComparison.Ordinal)
            || name.StartsWith("AWS__", StringComparison.Ordinal);

#if NET10_0
    private static void AddSqsQueues(
        IResourceBuilder<IStackResource> stack,
        string providerName,
        string serviceId,
        IReadOnlyList<(string Key, string? Value)> providerValues)
    {
        var values = providerValues.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var partitionCount = GetInt(values, "PartitionCount")
            ?? HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES;
        var fifoQueue = GetBool(values, "FifoQueue") ?? false;
        var receiveWaitTimeSeconds = GetInt(values, "ReceiveWaitTimeSeconds");
        var visibilityTimeoutSeconds = GetInt(values, "VisibilityTimeoutSeconds");
        var resourcePrefix = providerName.ToLowerInvariant();

        for (var partition = 0; partition < partitionCount; partition++)
        {
            stack.AddSQSQueue(
                $"{resourcePrefix}-{partition}",
                new QueueProps
                {
                    QueueName = GetQueueName(serviceId, providerName, partition, fifoQueue),
                    Fifo = fifoQueue,
                    ContentBasedDeduplication = fifoQueue,
                    DeduplicationScope = fifoQueue ? DeduplicationScope.MESSAGE_GROUP : null,
                    FifoThroughputLimit = fifoQueue ? FifoThroughputLimit.PER_MESSAGE_GROUP_ID : null,
                    ReceiveMessageWaitTime = receiveWaitTimeSeconds is { } waitTime
                        ? CdkDuration.Seconds(waitTime)
                        : null,
                    VisibilityTimeout = visibilityTimeoutSeconds is { } visibilityTimeout
                        ? CdkDuration.Seconds(visibilityTimeout)
                        : null,
                });
        }
    }

    private static int? GetInt(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;

    private static bool? GetBool(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var result)
            ? result
            : null;

    private static string GetQueueName(
        string serviceId,
        string providerName,
        int partition,
        bool fifoQueue)
        => $"{serviceId}-{providerName.ToLowerInvariant()}-{partition}{(fifoQueue ? ".fifo" : string.Empty)}";
#endif

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

    private sealed class SqsProviderConfiguration(
        SqsAwsConfiguration? aws,
        IReadOnlyList<IResourceBuilder<IResourceWithConnectionString>> connections,
        IEnumerable<(string Key, string? Value)> providerValues,
        IEnumerable<(string Key, string? Value)> environmentValues) : IProviderConfiguration
    {
        private readonly (string Key, string? Value)[] _providerValues = providerValues.ToArray();
        private readonly (string Key, string? Value)[] _environmentValues = environmentValues.ToArray();

        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder.WithEnvironment($"{prefix}__ProviderType", "SQS");
            if (aws is not null)
            {
                aws.ConfigureResource(resourceBuilder);
                if (aws.Region is { } region)
                {
                    resourceBuilder.WithEnvironment($"{prefix}__Region", region);
                }
            }

            foreach (var connection in connections)
            {
                resourceBuilder.WithReference(connection);
            }

            foreach (var (key, value) in _providerValues)
            {
                resourceBuilder.WithEnvironment(
                    $"{prefix}__{key.Replace(":", "__", StringComparison.Ordinal)}",
                    value);
            }

            foreach (var (key, value) in _environmentValues)
            {
                resourceBuilder.WithEnvironment(
                    key.Replace(":", "__", StringComparison.Ordinal),
                    value);
            }
        }
    }

    private abstract class SqsAwsConfiguration
    {
        public abstract string? Region { get; }

        public abstract void ConfigureResource<T>(IResourceBuilder<T> resourceBuilder)
            where T : IResourceWithEnvironment;
    }

#if NET10_0
    private sealed class OfficialAwsConfiguration(IAWSSDKConfig aws) : SqsAwsConfiguration
    {
        public override string? Region => aws.Region?.SystemName;

        public override void ConfigureResource<T>(IResourceBuilder<T> resourceBuilder)
            => resourceBuilder.WithReference(aws);
    }
#else
    private sealed class EnvironmentAwsConfiguration(string? profile, string? region) : SqsAwsConfiguration
    {
        public override string? Region => region;

        public override void ConfigureResource<T>(IResourceBuilder<T> resourceBuilder)
        {
            if (profile is not null)
            {
                resourceBuilder.WithEnvironment("AWS_PROFILE", profile);
            }

            if (region is not null)
            {
                resourceBuilder.WithEnvironment("AWS_REGION", region);
            }
        }
    }
#endif
}

internal enum SqsAspireResourceRole
{
    Silo,
    Client,
}

internal sealed class EnvironmentVariableScope : IDisposable
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
