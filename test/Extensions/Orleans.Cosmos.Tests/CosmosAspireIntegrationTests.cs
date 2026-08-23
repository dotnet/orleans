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
        var configuration = await CreateAspireConfigurationAsync(
            $"AccountEndpoint=https://localhost:8081/;AccountKey={CreateEmulatorKey()};");

        using var host = CreateHost(configuration);
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

        var configuration = await CreateAspireConfigurationAsync(
            $"AccountEndpoint={TestDefaultConfiguration.CosmosDBAccountEndpoint};AccountKey={TestDefaultConfiguration.CosmosDBAccountKey};");

        using var host = CreateHost(configuration);
        var options = host.Services.GetRequiredService<IOptions<CosmosClusteringOptions>>().Value;
        using var client = await options.CreateClient(host.Services);
        var account = await client.ReadAccountAsync();
        Assert.NotNull(account);
    }

    private static IHost CreateHost(IConfiguration configuration)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
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

    private static async Task<IConfigurationRoot> CreateAspireConfigurationAsync(string connectionString)
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
            .WithEnvironment("Orleans__Reminders__IsResourceCreationEnabled", "true")
            .WithEnvironment($"ConnectionStrings__{ResourceName}", connectionString);

        await using var app = await builder.BuildAsync();
        var configuration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: static key =>
                key.StartsWith("Orleans__", StringComparison.Ordinal)
                && !key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal)
                || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));

        AssertProvider(configuration, "Clustering", null);
        AssertProvider(configuration, "GrainStorage", "state");
        AssertProvider(configuration, "Reminders", null);
        return configuration;
    }

    private static void AssertProvider(IConfiguration configuration, string capability, string? name)
    {
        var path = name is null ? $"Orleans:{capability}" : $"Orleans:{capability}:{name}";
        Assert.Equal("AzureCosmosDB", configuration[$"{path}:ProviderType"]);
        Assert.Equal(ResourceName, configuration[$"{path}:ServiceKey"]);
    }

    private static string CreateEmulatorKey() => Convert.ToBase64String(new byte[64]);

}
