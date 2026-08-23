using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cosmos;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Reminders.Cosmos;
using TestExtensions;
using Xunit;

namespace Tester.Cosmos;

[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestCategory("Cosmos"), TestCategory("BVT")]
public sealed class CosmosAspireIntegrationTests
{
    private const string ResourceName = "orleans-cosmos";
    private const string DatabaseName = "OrleansAspireTests";

    [Fact]
    public async Task AspireAppModel_ActivatesClusteringStorageAndReminders()
    {
        var environment = await CreateAspireEnvironmentAsync();
        environment[$"ConnectionStrings:{ResourceName}"] =
            $"AccountEndpoint=https://localhost:8081/;AccountKey={CreateEmulatorKey()};";

        using var host = CreateHost(environment);
        var keyedClient = host.Services.GetRequiredKeyedService<CosmosClient>(ResourceName);
        var clustering = host.Services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value;
        var storage = host.Services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>().Get("state");
        var reminders = host.Services.GetRequiredService<IOptions<CosmosReminderTableOptions>>().Value;

        Assert.Same(keyedClient, await clustering.CreateClient(host.Services));
        Assert.Same(keyedClient, await storage.CreateClient(host.Services));
        Assert.Same(keyedClient, await reminders.CreateClient(host.Services));
        Assert.Equal(DatabaseName, clustering.DatabaseName);
        Assert.Equal(DatabaseName, storage.DatabaseName);
        Assert.Equal(DatabaseName, reminders.DatabaseName);
    }

    [Fact]
    public async Task AspireConfiguration_ConnectsToLiveCosmosDB()
    {
        CosmosTestUtils.CheckCosmosStorage();
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This test exercises the account-key configuration used by the Cosmos DB emulator CI job.");
        }

        var environment = await CreateAspireEnvironmentAsync();
        environment[$"ConnectionStrings:{ResourceName}"] =
            $"AccountEndpoint={TestDefaultConfiguration.CosmosDBAccountEndpoint};AccountKey={TestDefaultConfiguration.CosmosDBAccountKey};";

        using var host = CreateHost(environment);
        var options = host.Services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value;
        using var client = await options.CreateClient(host.Services);
        var account = await client.ReadAccountAsync();
        Assert.NotNull(account);
    }

    private static IHost CreateHost(Dictionary<string, string?> environment)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(environment);
        hostBuilder.AddKeyedAzureCosmosClient(
            ResourceName,
            settings =>
            {
                settings.DisableTracing = true;
            },
            options =>
            {
                options.ConnectionMode = ConnectionMode.Gateway;
                options.LimitToEndpoint = true;
                options.HttpClientFactory = () => new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                });
            });
        hostBuilder.UseOrleans();
        return hostBuilder.Build();
    }

    private static async Task<Dictionary<string, string?>> CreateAspireEnvironmentAsync()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB(ResourceName);
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(cosmos)
            .WithGrainStorage("state", cosmos)
            .WithReminders(cosmos);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment("Orleans__Clustering__DatabaseName", DatabaseName)
            .WithEnvironment("Orleans__Clustering__ContainerName", "Membership")
            .WithEnvironment("Orleans__Clustering__IsResourceCreationEnabled", "true")
            .WithEnvironment("Orleans__GrainStorage__state__DatabaseName", DatabaseName)
            .WithEnvironment("Orleans__GrainStorage__state__ContainerName", "State")
            .WithEnvironment("Orleans__GrainStorage__state__IsResourceCreationEnabled", "true")
            .WithEnvironment("Orleans__Reminders__DatabaseName", DatabaseName)
            .WithEnvironment("Orleans__Reminders__ContainerName", "Reminders")
            .WithEnvironment("Orleans__Reminders__IsResourceCreationEnabled", "true");

        await using var app = await builder.BuildAsync();
        var environment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);

        AssertProvider(environment, "Clustering", null);
        AssertProvider(environment, "GrainStorage", "state");
        AssertProvider(environment, "Reminders", null);
        return environment;
    }

    private static void AssertProvider(Dictionary<string, string?> environment, string capability, string? name)
    {
        var path = name is null ? $"Orleans:{capability}" : $"Orleans:{capability}:{name}";
        Assert.Equal("AzureCosmosDB", environment[$"{path}:ProviderType"]);
        Assert.Equal(ResourceName, environment[$"{path}:ServiceKey"]);
    }

    private static string CreateEmulatorKey() => Convert.ToBase64String(new byte[64]);

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
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }
}
