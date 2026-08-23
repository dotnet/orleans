using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Reminders.AzureStorage;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils;

[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("BVT")]
public sealed class AzureStorageAspireIntegrationTests
{
    private const string TablesResourceName = "orleans-tables";
    private const string BlobsResourceName = "orleans-blobs";
    private const string QueuesResourceName = "orleans-queues";

    [Fact]
    public async Task AspireAppModel_ActivatesAllAzureStorageProviders()
    {
        var configuration = await CreateAspireConfigurationAsync("UseDevelopmentStorage=true");

        using var host = CreateHost(configuration);
        var services = host.Services;
        var tableClient = services.GetRequiredKeyedService<TableServiceClient>(TablesResourceName);
        var blobClient = services.GetRequiredKeyedService<BlobServiceClient>(BlobsResourceName);
        var queueClient = services.GetRequiredKeyedService<QueueServiceClient>(QueuesResourceName);

        Assert.Same(
            tableClient,
            services.GetRequiredService<IOptions<AzureStorageClusteringOptions>>().Value.TableServiceClient);
        Assert.Same(
            tableClient,
            services.GetRequiredService<IOptionsMonitor<AzureTableStorageOptions>>().Get("table-state").TableServiceClient);
        Assert.Same(
            tableClient,
            services.GetRequiredService<IOptions<AzureTableReminderStorageOptions>>().Value.TableServiceClient);
        Assert.Same(
            tableClient,
            services.GetRequiredService<IOptionsMonitor<AzureTableGrainDirectoryOptions>>().Get("directory").TableServiceClient);
        Assert.Same(
            blobClient,
            services.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get("blob-state").BlobServiceClient);
        Assert.Same(
            queueClient,
            services.GetRequiredService<IOptionsMonitor<AzureQueueOptions>>().Get("queue-stream").QueueServiceClient);
    }

    [Fact]
    public async Task AspireConfiguration_ConnectsToLiveAzureStorageServices()
    {
        TestUtils.CheckForAzureStorage();
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This test exercises the connection-string configuration used by the Azurite CI job.");
        }

        var configuration = await CreateAspireConfigurationAsync(TestDefaultConfiguration.DataConnectionString);

        using var host = CreateHost(configuration);
        await host.Services
            .GetRequiredService<IOptions<AzureStorageClusteringOptions>>()
            .Value
            .TableServiceClient!
            .GetPropertiesAsync();
        await host.Services
            .GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>()
            .Get("blob-state")
            .BlobServiceClient!
            .GetPropertiesAsync();
        await host.Services
            .GetRequiredService<IOptionsMonitor<AzureQueueOptions>>()
            .Get("queue-stream")
            .QueueServiceClient!
            .GetPropertiesAsync();
    }

    private static IHost CreateHost(IConfiguration configuration)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
        hostBuilder.AddKeyedAzureTableServiceClient(TablesResourceName, settings =>
        {
            settings.DisableHealthChecks = true;
            settings.DisableTracing = true;
        });
        hostBuilder.AddKeyedAzureBlobServiceClient(BlobsResourceName, settings =>
        {
            settings.DisableHealthChecks = true;
            settings.DisableTracing = true;
        });
        hostBuilder.AddKeyedAzureQueueServiceClient(QueuesResourceName, settings =>
        {
            settings.DisableHealthChecks = true;
            settings.DisableTracing = true;
        });
        hostBuilder.UseOrleans();
        return hostBuilder.Build();
    }

    private static async Task<IConfigurationRoot> CreateAspireConfigurationAsync(string? connectionString)
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var storage = builder.AddAzureStorage("storage");
        var tables = storage.AddTables(TablesResourceName);
        var blobs = storage.AddBlobs(BlobsResourceName);
        var queues = storage.AddQueues(QueuesResourceName);
        var orleans = builder.AddOrleans("cluster")
            .WithClustering(tables)
            .WithGrainStorage("table-state", tables)
            .WithGrainStorage("blob-state", blobs)
            .WithReminders(tables)
            .WithGrainDirectory("directory", tables)
            .WithStreaming("queue-stream", queues);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment($"ConnectionStrings__{TablesResourceName}", connectionString)
            .WithEnvironment($"ConnectionStrings__{BlobsResourceName}", connectionString)
            .WithEnvironment($"ConnectionStrings__{QueuesResourceName}", connectionString);

        await using var app = await builder.BuildAsync();
        var configuration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: static key =>
                key.StartsWith("Orleans__", StringComparison.Ordinal)
                && !key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal)
                || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal));

        AssertProvider(configuration, "Clustering", null, "AzureTableStorage", TablesResourceName);
        AssertProvider(configuration, "GrainStorage", "table-state", "AzureTableStorage", TablesResourceName);
        AssertProvider(configuration, "GrainStorage", "blob-state", "AzureBlobStorage", BlobsResourceName);
        AssertProvider(configuration, "Reminders", null, "AzureTableStorage", TablesResourceName);
        AssertProvider(configuration, "GrainDirectory", "directory", "AzureTableStorage", TablesResourceName);
        AssertProvider(configuration, "Streaming", "queue-stream", "AzureQueueStorage", QueuesResourceName);
        return configuration;
    }

    private static void AssertProvider(
        IConfiguration configuration,
        string capability,
        string? name,
        string providerType,
        string serviceKey)
    {
        var path = name is null ? $"Orleans:{capability}" : $"Orleans:{capability}:{name}";
        Assert.Equal(providerType, configuration[$"{path}:ProviderType"]);
        Assert.Equal(serviceKey, configuration[$"{path}:ServiceKey"]);
    }
}
