using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Persistence;
using Orleans.Providers;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestCategory("Redis"), TestCategory("BVT")]
public sealed class RedisAspireIntegrationTests
{
    private const string ResourceName = "orleans-redis";

    [Theory]
    [InlineData(false, "Redis")]
    [InlineData(true, "AzureManagedRedis")]
    public async Task AspireAppModel_ProducesConfigurationForAllRedisCapabilities(
        bool useAzureManagedRedis,
        string expectedProviderType)
    {
        var environment = await CreateAspireEnvironmentAsync(useAzureManagedRedis, expectedProviderType);

        AssertProvider(environment, "Clustering", null, expectedProviderType);
        AssertProvider(environment, "GrainStorage", "state", expectedProviderType);
        AssertProvider(environment, "Reminders", null, expectedProviderType);
        AssertProvider(environment, "GrainDirectory", "directory", expectedProviderType);
        AssertProvider(environment, "Streaming", "stream", expectedProviderType);
        AssertProvider(environment, "GrainJournaling", null, expectedProviderType);
    }

    [Theory]
    [InlineData(false, "Redis")]
    [InlineData(true, "AzureManagedRedis")]
    public async Task AspireConfiguration_ActivatesProvidersAndConnectsToLiveRedis(
        bool useAzureManagedRedis,
        string expectedProviderType)
    {
        TestUtils.CheckForRedis();
        var environment = await CreateAspireEnvironmentAsync(useAzureManagedRedis, expectedProviderType);
        environment[$"ConnectionStrings:{ResourceName}"] = TestDefaultConfiguration.RedisConnectionString;

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(environment);
        hostBuilder.AddKeyedRedisClient(ResourceName, settings =>
        {
            settings.DisableAutoActivation = true;
            settings.DisableHealthChecks = true;
            settings.DisableTracing = true;
        });
        hostBuilder.UseOrleans();

        using var host = hostBuilder.Build();
        var services = host.Services;
        var keyedMultiplexer = services.GetRequiredKeyedService<IConnectionMultiplexer>(ResourceName);
        var clustering = services.GetRequiredService<IOptions<RedisClusteringOptions>>().Value;
        var storage = services.GetRequiredService<IOptionsMonitor<RedisStorageOptions>>().Get("state");
        var reminders = services.GetRequiredService<IOptions<RedisReminderTableOptions>>().Value;
        var directory = services.GetRequiredService<IOptionsMonitor<RedisGrainDirectoryOptions>>().Get("directory");
        var streaming = services.GetRequiredService<IOptionsMonitor<RedisStreamingOptions>>().Get("stream");

        AssertShared(keyedMultiplexer, await clustering.CreateMultiplexer(clustering));
        AssertShared(keyedMultiplexer, await storage.CreateMultiplexer(storage));
        AssertShared(keyedMultiplexer, await reminders.CreateMultiplexer(reminders));
        AssertShared(keyedMultiplexer, await directory.CreateMultiplexer(directory));
        AssertShared(keyedMultiplexer, await streaming.CreateMultiplexer(streaming));
        Assert.True(await keyedMultiplexer.GetDatabase().PingAsync() >= TimeSpan.Zero);
    }

    [Fact]
    public void Assembly_RegistersRedisAndAzureAliasesForGrainJournaling()
    {
        var registrations = typeof(RedisJournalStorageOptions)
            .Assembly
            .GetCustomAttributes<RegisterProviderAttribute>()
            .Where(attribute => attribute.Kind == "GrainJournaling")
            .Select(attribute => (attribute.Name, attribute.Target))
            .ToHashSet();

        Assert.Equal(3, registrations.Count);
        Assert.Contains(("Redis", "Silo"), registrations);
        Assert.Contains(("AzureRedisCache", "Silo"), registrations);
        Assert.Contains(("AzureManagedRedis", "Silo"), registrations);
    }

    private static void AssertShared(
        IConnectionMultiplexer expected,
        (IConnectionMultiplexer Multiplexer, bool IsShared) actual)
    {
        Assert.Same(expected, actual.Multiplexer);
        Assert.True(actual.IsShared);
    }

    private static async Task<Dictionary<string, string?>> CreateAspireEnvironmentAsync(
        bool useAzureManagedRedis,
        string providerType)
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        IResourceBuilder<IResourceWithConnectionString> redis;
        if (useAzureManagedRedis)
        {
            redis = builder.AddAzureManagedRedis(ResourceName);
        }
        else
        {
            redis = builder.AddRedis(ResourceName);
        }

        var orleans = ConfigureOrleans(builder, redis);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment("Orleans__GrainJournaling__ProviderType", providerType)
            .WithEnvironment("Orleans__GrainJournaling__ServiceKey", ResourceName);

        await using var app = await builder.BuildAsync();
        return await GetEnvironmentVariablesAsync(silo.Resource, app.Services);
    }

    private static OrleansService ConfigureOrleans(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<IResourceWithConnectionString> redis)
        => builder.AddOrleans("cluster")
            .WithClustering(redis)
            .WithGrainStorage("state", redis)
            .WithReminders(redis)
            .WithGrainDirectory("directory", redis)
            .WithStreaming("stream", redis);

    private static void AssertProvider(
        Dictionary<string, string?> environment,
        string capability,
        string? name,
        string providerType)
    {
        var path = name is null ? $"Orleans:{capability}" : $"Orleans:{capability}:{name}";
        Assert.Equal(providerType, environment[$"{path}:ProviderType"]);
        Assert.Equal(ResourceName, environment[$"{path}:ServiceKey"]);
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
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            if (!key.StartsWith("Orleans__", StringComparison.Ordinal)
                || key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal))
            {
                continue;
            }

            result[key.Replace("__", ":", StringComparison.Ordinal)] = value switch
            {
                IValueProvider valueProvider => await valueProvider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }
}
