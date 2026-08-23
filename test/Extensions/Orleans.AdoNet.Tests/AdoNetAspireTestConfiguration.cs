global using Orleans.GrainDirectory.AdoNet;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;

namespace UnitTests.AdoNet;

public enum AdoNetAspireDatabase
{
    SqlServer,
    PostgreSql,
    MySql,
    Oracle,
}

public enum AdoNetAspireCapability
{
    Clustering,
    GrainStorage,
    Reminders,
    GrainDirectory,
    Streaming,
}

internal sealed class AdoNetAspireTestConfiguration
{
    private static int s_resourceId;

    public static async Task<GeneratedConfiguration> CreateAsync(
        AdoNetAspireDatabase databaseType,
        IReadOnlyList<AdoNetAspireCapability> capabilities,
        string? explicitInvariant = null)
    {
        var id = Interlocked.Increment(ref s_resourceId);
        var stem = $"{databaseType.ToString().ToLowerInvariant()}-{id}";
        var databaseName = $"database-{stem}";
        var providerName = $"provider-{stem}";
        var clusterName = $"cluster-{stem}";

        await using var builder = DistributedApplicationTestingBuilder.Create();
        var database = AddDatabase(builder, databaseType, $"server-{stem}", databaseName);
        var orleans = builder.AddOrleans(clusterName);
        if (!capabilities.Contains(AdoNetAspireCapability.Clustering))
        {
            orleans.WithDevelopmentClustering();
        }

        foreach (var capability in capabilities)
        {
            if (explicitInvariant is null)
            {
                ConfigureCapability(orleans, database, capability, providerName);
            }
            else
            {
                ConfigureCapability(
                    orleans,
                    new ExplicitAdoNetProviderConfiguration(database, explicitInvariant),
                    capability,
                    providerName);
            }
        }

        var silo = builder.AddContainer($"silo-{stem}", "unused")
            .WithReference(orleans);

        await using var app = await builder.BuildAsync();
        var rawEnvironment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);
        var normalizedEnvironment = rawEnvironment.ToDictionary(
            pair => pair.Key.Replace("__", ":", StringComparison.Ordinal),
            pair => pair.Value,
            StringComparer.Ordinal);
        var hostConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(normalizedEnvironment)
            .Build();

        return new GeneratedConfiguration(
            databaseType,
            capabilities,
            databaseName,
            providerName,
            rawEnvironment,
            hostConfiguration);
    }

    private static IResourceBuilder<IResourceWithConnectionString> AddDatabase(
        IDistributedApplicationBuilder builder,
        AdoNetAspireDatabase databaseType,
        string serverName,
        string databaseName)
        => databaseType switch
        {
            AdoNetAspireDatabase.SqlServer => AddSqlServerDatabase(builder, serverName, databaseName),
            AdoNetAspireDatabase.PostgreSql => AddPostgresDatabase(builder, serverName, databaseName),
            AdoNetAspireDatabase.MySql => AddMySqlDatabase(builder, serverName, databaseName),
            AdoNetAspireDatabase.Oracle => AddOracleDatabase(builder, serverName, databaseName),
            _ => throw new ArgumentOutOfRangeException(nameof(databaseType), databaseType, null),
        };

    private static IResourceBuilder<IResourceWithConnectionString> AddSqlServerDatabase(
        IDistributedApplicationBuilder builder,
        string serverName,
        string databaseName)
    {
        var server = builder.AddSqlServer(serverName);
        AllocateEndpoint(server.Resource, 1433);
        return server.AddDatabase(databaseName);
    }

    private static IResourceBuilder<IResourceWithConnectionString> AddPostgresDatabase(
        IDistributedApplicationBuilder builder,
        string serverName,
        string databaseName)
    {
        var server = builder.AddPostgres(serverName);
        AllocateEndpoint(server.Resource, 5432);
        return server.AddDatabase(databaseName);
    }

    private static IResourceBuilder<IResourceWithConnectionString> AddMySqlDatabase(
        IDistributedApplicationBuilder builder,
        string serverName,
        string databaseName)
    {
        var server = builder.AddMySql(serverName);
        AllocateEndpoint(server.Resource, 3306);
        return server.AddDatabase(databaseName);
    }

    private static IResourceBuilder<IResourceWithConnectionString> AddOracleDatabase(
        IDistributedApplicationBuilder builder,
        string serverName,
        string databaseName)
    {
        var server = builder.AddOracle(serverName);
        AllocateEndpoint(server.Resource, 1521);
        return server.AddDatabase(databaseName);
    }

    private static void AllocateEndpoint(IResource resource, int port)
    {
        var endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port);
    }

    private static void ConfigureCapability(
        OrleansService orleans,
        IResourceBuilder<IResourceWithConnectionString> database,
        AdoNetAspireCapability capability,
        string providerName)
    {
        switch (capability)
        {
            case AdoNetAspireCapability.Clustering:
                orleans.WithClustering(database);
                break;
            case AdoNetAspireCapability.GrainStorage:
                orleans.WithGrainStorage(providerName, database);
                break;
            case AdoNetAspireCapability.Reminders:
                orleans.WithReminders(database);
                break;
            case AdoNetAspireCapability.GrainDirectory:
                orleans.WithGrainDirectory(providerName, database);
                break;
            case AdoNetAspireCapability.Streaming:
                orleans.WithStreaming(providerName, database);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(capability), capability, null);
        }
    }

    private static void ConfigureCapability(
        OrleansService orleans,
        IProviderConfiguration provider,
        AdoNetAspireCapability capability,
        string providerName)
    {
        switch (capability)
        {
            case AdoNetAspireCapability.Clustering:
                orleans.WithClustering(provider);
                break;
            case AdoNetAspireCapability.GrainStorage:
                orleans.WithGrainStorage(providerName, provider);
                break;
            case AdoNetAspireCapability.Reminders:
                orleans.WithReminders(provider);
                break;
            case AdoNetAspireCapability.GrainDirectory:
                orleans.WithGrainDirectory(providerName, provider);
                break;
            case AdoNetAspireCapability.Streaming:
                orleans.WithStreaming(providerName, provider);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(capability), capability, null);
        }
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
            await annotation.Callback(callbackContext);
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

            result[key] = value switch
            {
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }

    private static bool IsRelevantEnvironmentVariable(string name)
        => name.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__GrainStorage__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__Reminders__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__GrainDirectory__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || name.StartsWith("ConnectionStrings__", StringComparison.Ordinal);

    private sealed class ExplicitAdoNetProviderConfiguration(
        IResourceBuilder<IResourceWithConnectionString> database,
        string invariant) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configurationSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = configurationSectionPath.Replace(":", "__", StringComparison.Ordinal);
            resourceBuilder
                .WithEnvironment($"Orleans__{prefix}__ProviderType", "AdoNet")
                .WithEnvironment($"Orleans__{prefix}__Invariant", invariant)
                .WithEnvironment($"Orleans__{prefix}__ServiceKey", database.Resource.Name)
                .WithReference(database);
        }
    }

    internal sealed record GeneratedConfiguration(
        AdoNetAspireDatabase DatabaseType,
        IReadOnlyList<AdoNetAspireCapability> Capabilities,
        string DatabaseName,
        string ProviderName,
        IReadOnlyDictionary<string, string?> RawEnvironment,
        IConfigurationRoot HostConfiguration) : IDisposable
    {
        public AdoNetAspireCapability Capability => Capabilities.Single();

        public void Dispose() => (HostConfiguration as IDisposable)?.Dispose();
    }
}
