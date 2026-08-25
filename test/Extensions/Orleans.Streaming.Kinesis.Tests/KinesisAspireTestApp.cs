// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
#if NET10_0_OR_GREATER
using Aspire.Hosting.Testing;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Orleans.Streaming.Kinesis.Tests;

internal sealed class KinesisAspireTestApp : IAsyncDisposable
{
    private const string SiloResourceName = "silo";
    private const string ClientResourceName = "client";
    private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(10);
    private readonly IAsyncDisposable? _builder;
    private readonly DistributedApplication _application;
    private readonly IResource _silo;
    private readonly IResource _client;

    private KinesisAspireTestApp(
        IAsyncDisposable? builder,
        DistributedApplication application,
        KinesisAspireContractResources resources)
    {
        _builder = builder;
        _application = application;
        _silo = resources.Silo;
        _client = resources.Client;
        Topology = resources.Topology;
    }

    public KinesisAspireTopologySpecification Topology { get; }

    public string ProviderName => Topology.ProviderName;

    public string ServiceId => Topology.ServiceId;

    public DistributedApplicationModel Model
        => _application.Services.GetRequiredService<DistributedApplicationModel>();

    public static IDistributedApplicationBuilder CreateBuilder()
    {
#if NET10_0_OR_GREATER
        return DistributedApplicationTestingBuilder.Create();
#else
        return DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                Args = [],
                DisableDashboard = true,
            });
#endif
    }

    public static async Task<KinesisAspireTestApp> CreateAsync()
    {
        var builder = CreateBuilder();
#if NET10_0_OR_GREATER
        IAsyncDisposable? builderLifetime = (IAsyncDisposable)builder;
#else
        IAsyncDisposable? builderLifetime = null;
#endif
        var resources = AddOfficialAwsGeneratedContract(builder);
#if NET10_0_OR_GREATER
        var application = await ((IDistributedApplicationTestingBuilder)builder).BuildAsync();
#else
        var application = builder.Build();
#endif

        return new KinesisAspireTestApp(builderLifetime, application, resources);
    }

    public static KinesisAspireContractResources AddOfficialAwsGeneratedContract(
        IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var topology = KinesisAspireTopologySpecification.Official;
        var stream = builder.AddResource(new KinesisStreamResource(topology.Stream));
        var pubSubTable = builder.AddResource(new DynamoDbTableResource(topology.PubSubTable));
        var checkpointTable = builder.AddResource(new DynamoDbTableResource(topology.CheckpointTable));

        var streamArn = new ResourceOutputAnnotation(
            "StreamArn",
            ReferenceExpression.Create($"{topology.StreamArn}"));
        var pubSubTableName = new ResourceOutputAnnotation(
            "TableName",
            ReferenceExpression.Create($"{topology.PubSubTable.TableName}"));
        var checkpointTableName = new ResourceOutputAnnotation(
            "TableName",
            ReferenceExpression.Create($"{topology.CheckpointTable.TableName}"));
        stream.Resource.Annotations.Add(streamArn);
        pubSubTable.Resource.Annotations.Add(pubSubTableName);
        checkpointTable.Resource.Annotations.Add(checkpointTableName);

        var streaming = new KinesisProviderConfiguration(
            topology,
            streamArn,
            checkpointTableName);
        var pubSubStorage = new DynamoDbGrainStorageProviderConfiguration(
            topology,
            pubSubTableName);
        var orleans = builder.AddOrleans(topology.StackName)
            .WithClusterId(topology.ClusterId)
            .WithServiceId(topology.ServiceId)
            .WithClustering(new TestClusteringConfiguration())
            .WithGrainStorage("PubSubStore", pubSubStorage)
            .WithStreaming(topology.ProviderName, streaming);
        var silo = builder.AddContainer(SiloResourceName, "unused")
            .WithReference(orleans);
        var client = builder.AddContainer(ClientResourceName, "unused")
            .WithReference(orleans.AsClient());

        return new KinesisAspireContractResources(
            topology,
            stream.Resource,
            pubSubTable.Resource,
            checkpointTable.Resource,
            silo.Resource,
            client.Resource);
    }

    public Task<IReadOnlyDictionary<string, string?>> GetSiloEnvironmentAsync(
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
        => GetEnvironmentVariablesAsync(_silo, includeClustering: false, operation);

    public Task<IReadOnlyDictionary<string, string?>> GetClientEnvironmentAsync(
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
        => GetEnvironmentVariablesAsync(_client, includeClustering: false, operation);

    public async Task<IReadOnlyDictionary<string, string?>> ResolveEnvironmentAsync(
        KinesisAspireResourceRole role,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
    {
        var environment = role == KinesisAspireResourceRole.Silo
            ? await GetSiloEnvironmentAsync(operation)
            : await GetClientEnvironmentAsync(operation);
        return NormalizeConfiguration(environment);
    }

    public async Task<EnvironmentVariableScope> CreateEnvironmentScopeAsync(
        KinesisAspireResourceRole role,
        bool streamingOnly = false)
    {
        var resource = role == KinesisAspireResourceRole.Silo ? _silo : _client;
        var values = await GetEnvironmentVariablesAsync(resource, includeClustering: true);
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

    public async Task<IHost> CreateSiloHost(
        Action<IServiceCollection>? configureServices = null)
    {
        using var environment = await CreateEnvironmentScopeAsync(KinesisAspireResourceRole.Silo);
        var hostBuilder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(hostBuilder.Services);
        hostBuilder.UseOrleans();
        return hostBuilder.Build();
    }

    public async Task<IHost> CreateClientHost(
        Action<IServiceCollection>? configureServices = null)
    {
        using var environment = await CreateEnvironmentScopeAsync(KinesisAspireResourceRole.Client);
        var hostBuilder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(hostBuilder.Services);
        hostBuilder.UseOrleansClient();
        return hostBuilder.Build();
    }

    public Task<IHost> BuildSiloHostAsync(
        Action<IServiceCollection>? configureServices = null)
        => CreateSiloHost(configureServices);

    public Task<IHost> BuildClientHostAsync(
        Action<IServiceCollection>? configureServices = null)
        => CreateClientHost(configureServices);

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
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in environment)
        {
            var normalizedKey = key.StartsWith("Orleans__", StringComparison.Ordinal)
                    || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
                    || key.StartsWith("AWS__", StringComparison.Ordinal)
                ? key.Replace("__", ":", StringComparison.Ordinal)
                : key;
            if (!result.TryAdd(normalizedKey, value))
            {
                throw new InvalidOperationException(
                    $"Environment keys normalize to duplicate configuration key '{normalizedKey}'.");
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, string?>> GetEnvironmentVariablesAsync(
        IResource resource,
        bool includeClustering,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(operation)
            {
                ServiceProvider = _application.Services,
            });
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var annotationValues = new Dictionary<string, object>(StringComparer.Ordinal);
            var callbackContext = new EnvironmentCallbackContext(
                executionContext,
                resource,
                annotationValues);
            await annotation.Callback(callbackContext).WaitAsync(ResolutionTimeout);
            foreach (var (key, value) in annotationValues)
            {
                if (!values.TryAdd(key, value))
                {
                    throw new InvalidOperationException(
                        $"Resource '{resource.Name}' defines duplicate environment key '{key}'.");
                }
            }
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
            if (!IsRelevantEnvironmentVariable(key, includeClustering))
            {
                continue;
            }

            try
            {
                result.Add(
                    key,
                    value switch
                    {
                        IValueProvider provider => await provider
                            .GetValueAsync(valueContext)
                            .AsTask()
                            .WaitAsync(ResolutionTimeout),
                        _ => value.ToString(),
                    });
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

    private static bool IsRelevantEnvironmentVariable(string name, bool includeClustering)
        => name.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__GrainStorage__", StringComparison.Ordinal)
            || includeClustering
                && name.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || name is "Orleans__ClusterId" or "Orleans__ServiceId"
            || name.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
            || name.StartsWith("AWS_", StringComparison.Ordinal)
            || name.StartsWith("AWS__", StringComparison.Ordinal);

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

    private sealed class KinesisProviderConfiguration(
        KinesisAspireTopologySpecification topology,
        ResourceOutputAnnotation streamArn,
        ResourceOutputAnnotation checkpointTableName) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder
                .WithEnvironment($"{prefix}__ProviderType", "Kinesis")
                .WithEnvironment($"{prefix}__ServiceKey", topology.Stream.ResourceName)
                .WithEnvironment($"{prefix}__StreamName", topology.Stream.StreamName)
                .WithEnvironment($"{prefix}__Region", topology.Region)
                .WithEnvironment(
                    $"AWS__Resources__{topology.Stream.ResourceName}__{streamArn.Name}",
                    streamArn.Value)
                .WithEnvironment(context =>
                {
                    if (context.ExecutionContext.IsPublishMode)
                    {
                        return;
                    }

                    context.EnvironmentVariables["AWS_PROFILE"] = topology.Profile;
                    context.EnvironmentVariables["AWS_REGION"] = topology.Region;
                    context.EnvironmentVariables["AWS__Profile"] = topology.Profile;
                    context.EnvironmentVariables["AWS__Region"] = topology.Region;
                });

            if (resourceBuilder.Resource.Name == SiloResourceName)
            {
                resourceBuilder
                    .WithEnvironment($"{prefix}__Checkpoint__Type", "DynamoDB")
                    .WithEnvironment(
                        $"{prefix}__Checkpoint__ServiceKey",
                        topology.CheckpointTable.ResourceName)
                    .WithEnvironment($"{prefix}__Checkpoint__Region", topology.Region)
                    .WithEnvironment($"{prefix}__Checkpoint__CreateIfNotExists", "false")
                    .WithEnvironment(
                        $"{prefix}__Checkpoint__UseProvisionedThroughput",
                        "false")
                    .WithEnvironment(
                        $"AWS__Resources__{topology.CheckpointTable.ResourceName}__{checkpointTableName.Name}",
                        checkpointTableName.Value);
            }
        }
    }

    private sealed class DynamoDbGrainStorageProviderConfiguration(
        KinesisAspireTopologySpecification topology,
        ResourceOutputAnnotation tableName) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder
                .WithEnvironment($"{prefix}__ProviderType", "DynamoDB")
                .WithEnvironment($"{prefix}__ServiceKey", topology.PubSubTable.ResourceName)
                .WithEnvironment($"{prefix}__ServiceId", topology.ServiceId)
                .WithEnvironment($"{prefix}__UseProvisionedThroughput", "false")
                .WithEnvironment($"{prefix}__CreateIfNotExists", "false")
                .WithEnvironment($"{prefix}__UpdateIfExists", "false")
                .WithEnvironment(
                    $"AWS__Resources__{topology.PubSubTable.ResourceName}__{tableName.Name}",
                    tableName.Value);
        }
    }
}

internal enum KinesisAspireResourceRole
{
    Silo,
    Client,
}

internal sealed class EnvironmentVariableScope : IDisposable
{
    private static readonly string[] AmbientAwsVariables =
    [
        "AWS_REGION",
        "AWS_DEFAULT_REGION",
        "AWS_PROFILE",
        "AWS__Region",
        "AWS__Profile",
        "AWS_ENDPOINT_URL_KINESIS",
        "AWS_ENDPOINT_URL_DYNAMODB",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
    ];

    private readonly Dictionary<string, PreviousEnvironmentValue> _previousValues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _touchOrder = [];
    private bool _disposed;

    public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        try
        {
            foreach (var key in AmbientAwsVariables)
            {
                SaveAndSet(key, null);
            }

            foreach (var (key, value) in values)
            {
                SaveAndSet(key, value);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var index = _touchOrder.Count - 1; index >= 0; index--)
        {
            var key = _touchOrder[index];
            var previous = _previousValues[key];
            Environment.SetEnvironmentVariable(
                key,
                previous.Existed ? previous.Value : null);
        }
    }

    private void SaveAndSet(string key, string? value)
    {
        if (!_previousValues.ContainsKey(key))
        {
            var previous = Environment.GetEnvironmentVariable(key);
            _previousValues.Add(
                key,
                new PreviousEnvironmentValue(previous is not null, previous));
            _touchOrder.Add(key);
        }

        Environment.SetEnvironmentVariable(key, value);
    }

    private readonly record struct PreviousEnvironmentValue(bool Existed, string? Value);
}

internal sealed record KinesisAspireTopologySpecification(
    string StackName,
    string ClusterId,
    string ServiceId,
    string ProviderName,
    string Profile,
    string Region,
    string AccountId,
    KinesisStreamSpecification Stream,
    DynamoDbTableSpecification PubSubTable,
    DynamoDbTableSpecification CheckpointTable)
{
    public static KinesisAspireTopologySpecification Official { get; } = new(
        StackName: "orders-kinesis",
        ClusterId: "orders-v1",
        ServiceId: "orders-service",
        ProviderName: "Orders",
        Profile: "orders-profile",
        Region: "us-west-2",
        AccountId: "<account>",
        Stream: new KinesisStreamSpecification(
            ResourceName: "orders-stream",
            StreamName: "orleans-orders",
            ShardCount: 4,
            CapacityMode: "PROVISIONED",
            RetentionHours: 24,
            RemovalPolicy: "RETAIN"),
        PubSubTable: new DynamoDbTableSpecification(
            ResourceName: "orders-pubsub",
            TableName: "orleans-orders-pubsub",
            PartitionKey: new DynamoDbKeySpecification("GrainReference", "HASH", "S"),
            SortKey: new DynamoDbKeySpecification("GrainType", "RANGE", "S"),
            BillingMode: "PAY_PER_REQUEST",
            RemovalPolicy: "RETAIN"),
        CheckpointTable: new DynamoDbTableSpecification(
            ResourceName: "orders-checkpoints",
            TableName: "orleans-orders-checkpoints",
            PartitionKey: new DynamoDbKeySpecification("CheckpointNamespace", "HASH", "S"),
            SortKey: new DynamoDbKeySpecification("Partition", "RANGE", "S"),
            BillingMode: "PAY_PER_REQUEST",
            RemovalPolicy: "RETAIN"));

    public string StreamArn
        => $"arn:aws:kinesis:{Region}:{AccountId}:stream/{Stream.StreamName}";
}

internal sealed record KinesisStreamSpecification(
    string ResourceName,
    string StreamName,
    int ShardCount,
    string CapacityMode,
    int RetentionHours,
    string RemovalPolicy);

internal sealed record DynamoDbKeySpecification(
    string AttributeName,
    string KeyType,
    string AttributeType);

internal sealed record DynamoDbTableSpecification(
    string ResourceName,
    string TableName,
    DynamoDbKeySpecification PartitionKey,
    DynamoDbKeySpecification SortKey,
    string BillingMode,
    string RemovalPolicy);

internal sealed class KinesisStreamResource(KinesisStreamSpecification specification)
    : Resource(specification.ResourceName)
{
    public KinesisStreamSpecification Specification { get; } = specification;
}

internal sealed class DynamoDbTableResource(DynamoDbTableSpecification specification)
    : Resource(specification.ResourceName)
{
    public DynamoDbTableSpecification Specification { get; } = specification;
}

internal sealed record ResourceOutputAnnotation(
    string Name,
    ReferenceExpression Value) : IResourceAnnotation;

internal sealed record KinesisAspireContractResources(
    KinesisAspireTopologySpecification Topology,
    KinesisStreamResource Stream,
    DynamoDbTableResource PubSubTable,
    DynamoDbTableResource CheckpointTable,
    IResource Silo,
    IResource Client);
