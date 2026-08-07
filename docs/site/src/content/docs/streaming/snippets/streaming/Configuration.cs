using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Orleans.Docs.Snippets.Streaming;

public static class StreamConfiguration
{
    // <memory_silo>
    public static IHostApplicationBuilder AddStreamingSilo(
        this IHostApplicationBuilder builder)
    {
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .AddMemoryStreams(TemperatureStreams.ProviderName)
                .AddMemoryGrainStorage("PubSubStore");
        });

        return builder;
    }
    // </memory_silo>

    // <memory_client>
    public static IHostApplicationBuilder AddStreamingClient(
        this IHostApplicationBuilder builder)
    {
        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder.AddMemoryStreams(TemperatureStreams.ProviderName);
        });

        return builder;
    }
    // </memory_client>

    public static void ConfigurePubSubManagedIdentity(
        IHostApplicationBuilder hostBuilder,
        IConfiguration configuration)
    {
        // <pubsub_managed_identity>
        var endpoint = new Uri(configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!);
        var credential = new DefaultAzureCredential();

        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableGrainStorage(
                "PubSubStore",
                options => options.TableServiceClient =
                    new TableServiceClient(endpoint, credential));
        });
        // </pubsub_managed_identity>
    }

    public static void ConfigurePubSubConnectionString(
        IHostApplicationBuilder hostBuilder,
        string connectionString)
    {
        // <pubsub_connection_string>
        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableGrainStorage(
                "PubSubStore",
                options => options.TableServiceClient =
                    new TableServiceClient(connectionString));
        });
        // </pubsub_connection_string>
    }

    public static void ConfigureAzureQueueManagedIdentity(
        IHostApplicationBuilder hostBuilder,
        IConfiguration configuration)
    {
        // <azure_queue_managed_identity>
        var queueEndpoint =
            new Uri(configuration["AZURE_QUEUE_STORAGE_ENDPOINT"]!);
        var credential = new DefaultAzureCredential();

        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .AddAzureQueueStreams(
                    TemperatureStreams.ProviderName,
                    streams => streams.ConfigureAzureQueue(
                        optionsBuilder => optionsBuilder.Configure(options =>
                            options.QueueServiceClient =
                                new QueueServiceClient(queueEndpoint, credential))))
                .AddAzureTableGrainStorage(
                    "PubSubStore",
                    options => options.TableServiceClient =
                        new TableServiceClient(
                            new Uri(configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!),
                            credential));
        });
        // </azure_queue_managed_identity>
    }

    public static void ConfigureAzureQueueConnectionString(
        IHostApplicationBuilder hostBuilder,
        string connectionString)
    {
        // <azure_queue_connection_string>
        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .AddAzureQueueStreams(
                    TemperatureStreams.ProviderName,
                    streams => streams.ConfigureAzureQueue(
                        optionsBuilder => optionsBuilder.Configure(options =>
                            options.QueueServiceClient =
                                new QueueServiceClient(connectionString))))
                .AddAzureTableGrainStorage(
                    "PubSubStore",
                    options => options.TableServiceClient =
                        new TableServiceClient(connectionString));
        });
        // </azure_queue_connection_string>
    }
}
