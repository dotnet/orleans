using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Orleans.Docs.Snippets.Streaming;

public static class StreamConfiguration
{
    public static void ConfigurePubSubManagedIdentity(
        IHostApplicationBuilder hostBuilder, IConfiguration configuration)
    {
        // <pubsub_managed_identity>
        var endpoint = new Uri(configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!);
        var credential = new DefaultAzureCredential();

        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableGrainStorage("PubSubStore",
                options => options.TableServiceClient = new TableServiceClient(endpoint, credential));
        });
        // </pubsub_managed_identity>
    }

    public static void ConfigurePubSubConnectionString(
        IHostApplicationBuilder hostBuilder, string connectionString)
    {
        // <pubsub_connection_string>
        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddAzureTableGrainStorage("PubSubStore",
                options => options.TableServiceClient = new TableServiceClient(connectionString));
        });
        // </pubsub_connection_string>
    }

    public static void ConfigureStreamProviderManagedIdentity(
        IHostApplicationBuilder hostBuilder, IConfiguration configuration)
    {
        // <stream_provider_managed_identity>
        var tableEndpoint = new Uri(configuration["AZURE_TABLE_STORAGE_ENDPOINT"]!);
        var queueEndpoint = new Uri(configuration["AZURE_QUEUE_STORAGE_ENDPOINT"]!);
        var credential = new DefaultAzureCredential();

        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddMemoryStreams("StreamProvider")
                .AddAzureQueueStreams("AzureQueueProvider",
                    optionsBuilder => optionsBuilder.ConfigureAzureQueue(
                        options => options.Configure(
                            opt => opt.QueueServiceClient = new QueueServiceClient(queueEndpoint, credential))))
                .AddAzureTableGrainStorage("PubSubStore",
                    options => options.TableServiceClient = new TableServiceClient(tableEndpoint, credential));
        });
        // </stream_provider_managed_identity>
    }

    public static void ConfigureStreamProviderConnectionString(
        IHostApplicationBuilder hostBuilder, string connectionString)
    {
        // <stream_provider_connection_string>
        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.AddMemoryStreams("StreamProvider")
                .AddAzureQueueStreams("AzureQueueProvider",
                    optionsBuilder => optionsBuilder.ConfigureAzureQueue(
                        options => options.Configure(
                            opt => opt.QueueServiceClient = new QueueServiceClient(connectionString))))
                .AddAzureTableGrainStorage("PubSubStore",
                    options => options.TableServiceClient = new TableServiceClient(connectionString));
        });
        // </stream_provider_connection_string>
    }
}
