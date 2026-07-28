using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace ClientConfig;

// Dummy grain interface for application parts example
public interface IValueGrain : IGrainWithIntegerKey
{
    Task<int> GetValue();
}

public class Configuration
{
    // <full_client_config>
    public static async Task ConfigureClient(string connectionString)
    {
        var client = new ClientBuilder()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "my-first-cluster";
                options.ServiceId = "MyOrleansService";
            })
            .UseAzureStorageClustering(
                options => options.ConfigureTableServiceClient(connectionString))
            .ConfigureApplicationParts(
                parts => parts.AddApplicationPart(
                    typeof(IValueGrain).Assembly))
            .Build();

        await client.Connect();
    }
    // </full_client_config>

    // <cluster_options>
    public static void ConfigureClusterOptions(IClientBuilder clientBuilder)
    {
        clientBuilder.Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "orleans-docker";
            options.ServiceId = "AspNetSampleApp";
        });
    }
    // </cluster_options>

    // <azure_clustering>
    public static void ConfigureAzureClustering(IClientBuilder clientBuilder, string connectionString)
    {
        clientBuilder.UseAzureStorageClustering(
            options => options.ConfigureTableServiceClient(connectionString));
    }
    // </azure_clustering>

    // <application_parts>
    public static void ConfigureApplicationParts(IClientBuilder clientBuilder)
    {
        clientBuilder.ConfigureApplicationParts(
            parts => parts.AddApplicationPart(
                typeof(IValueGrain).Assembly)
                .WithReferences());
    }
    // </application_parts>
}
