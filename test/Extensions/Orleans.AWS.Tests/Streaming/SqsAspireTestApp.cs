#if NET10_0
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace AWSUtils.Tests.Streaming;

internal sealed class SqsAspireTestApp : IAsyncDisposable
{
    private const string SiloResourceName = "silo";
    private const string ClientResourceName = "client";
    private readonly IAsyncDisposable _builder;
    private readonly DistributedApplication _application;
    private readonly IResource _silo;
    private readonly IResource _client;

    private SqsAspireTestApp(
        IAsyncDisposable builder,
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
        var builder = DistributedApplicationTestingBuilder.Create();
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

        var provider = new SqsProviderConfiguration(
            awsProfile,
            awsRegion,
            connectionResources,
            providerValues,
            environmentValues);
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(new TestClusteringConfiguration())
            .WithServiceId(serviceId)
            .WithStreaming(providerName, provider);
        var silo = builder.AddContainer(SiloResourceName, "unused")
            .WithReference(orleans);
        var client = builder.AddContainer(ClientResourceName, "unused")
            .WithReference(orleans.AsClient());
        var application = await builder.BuildAsync();

        return new SqsAspireTestApp(
            builder,
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
        await _builder.DisposeAsync();
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
        string? awsProfile,
        string? awsRegion,
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
            if (awsProfile is not null)
            {
                resourceBuilder
                    .WithEnvironment("AWS_PROFILE", awsProfile)
                    .WithEnvironment("AWS__Profile", awsProfile);
            }

            if (awsRegion is not null)
            {
                resourceBuilder
                    .WithEnvironment("AWS_REGION", awsRegion)
                    .WithEnvironment("AWS__Region", awsRegion);
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
#endif
