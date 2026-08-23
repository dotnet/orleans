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
        var environment = await CreateAspireEnvironmentAsync();
        environment[$"ConnectionStrings:{TablesResourceName}"] = "UseDevelopmentStorage=true";
        environment[$"ConnectionStrings:{BlobsResourceName}"] = "UseDevelopmentStorage=true";
        environment[$"ConnectionStrings:{QueuesResourceName}"] = "UseDevelopmentStorage=true";

        using var host = CreateHost(environment);
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

        var environment = await CreateAspireEnvironmentAsync();
        environment[$"ConnectionStrings:{TablesResourceName}"] = TestDefaultConfiguration.DataConnectionString;
        environment[$"ConnectionStrings:{BlobsResourceName}"] = TestDefaultConfiguration.DataConnectionString;
        environment[$"ConnectionStrings:{QueuesResourceName}"] = TestDefaultConfiguration.DataConnectionString;

        using var host = CreateHost(environment);
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

    private static IHost CreateHost(Dictionary<string, string?> environment)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(environment);
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

    private static async Task<Dictionary<string, string?>> CreateAspireEnvironmentAsync()
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
        var silo = builder.AddContainer("silo", "unused").WithReference(orleans);

        await using var app = await builder.BuildAsync();
        var environment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);

        AssertProvider(environment, "Clustering", null, "AzureTableStorage", TablesResourceName);
        AssertProvider(environment, "GrainStorage", "table-state", "AzureTableStorage", TablesResourceName);
        AssertProvider(environment, "GrainStorage", "blob-state", "AzureBlobStorage", BlobsResourceName);
        AssertProvider(environment, "Reminders", null, "AzureTableStorage", TablesResourceName);
        AssertProvider(environment, "GrainDirectory", "directory", "AzureTableStorage", TablesResourceName);
        AssertProvider(environment, "Streaming", "queue-stream", "AzureQueueStorage", QueuesResourceName);
        return environment;
    }

    private static void AssertProvider(
        Dictionary<string, string?> environment,
        string capability,
        string? name,
        string providerType,
        string serviceKey)
    {
        var path = name is null ? $"Orleans:{capability}" : $"Orleans:{capability}:{name}";
        Assert.Equal(providerType, environment[$"{path}:ProviderType"]);
        Assert.Equal(serviceKey, environment[$"{path}:ServiceKey"]);
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
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }
}
