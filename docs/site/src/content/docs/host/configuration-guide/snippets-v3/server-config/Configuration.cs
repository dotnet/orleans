using System.Net;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace ServerConfig;

// Dummy grain for application parts example
public interface IValueGrain : IGrainWithIntegerKey
{
    Task<int> GetValue();
}

public class ValueGrain : Grain, IValueGrain
{
    public Task<int> GetValue() => Task.FromResult(42);
}

public class Configuration
{
    // <full_silo_config>
    public static async Task ConfigureSilo(string connectionString)
    {
        var siloHostBuilder = new SiloHostBuilder()
            .UseAzureStorageClustering(
                options => options.ConfigureTableServiceClient(connectionString))
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "my-first-cluster";
                options.ServiceId = "AspNetSampleApp";
            })
            .ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000)
            .ConfigureApplicationParts(
                parts => parts.AddApplicationPart(typeof(ValueGrain).Assembly).WithReferences());

        var silo = siloHostBuilder.Build();
        await silo.StartAsync();
    }
    // </full_silo_config>

    // <azure_clustering>
    public static void ConfigureAzureClustering(ISiloHostBuilder siloBuilder, string connectionString)
    {
        siloBuilder.UseAzureStorageClustering(
            options => options.ConfigureTableServiceClient(connectionString));
    }
    // </azure_clustering>

    // <cluster_options>
    public static void ConfigureClusterOptions(ISiloHostBuilder siloBuilder)
    {
        siloBuilder.Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "my-first-cluster";
            options.ServiceId = "AspNetSampleApp";
        });
    }
    // </cluster_options>

    // <configure_endpoints>
    public static void ConfigureSimpleEndpoints(ISiloHostBuilder siloBuilder)
    {
        siloBuilder.ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000);
    }
    // </configure_endpoints>

    // <endpoint_options>
    public static void ConfigureEndpointOptions(ISiloHostBuilder siloBuilder)
    {
        siloBuilder.Configure<EndpointOptions>(options =>
        {
            // Port to use for silo-to-silo
            options.SiloPort = 11111;
            // Port to use for the gateway
            options.GatewayPort = 30000;
            // IP Address to advertise in the cluster
            options.AdvertisedIPAddress = IPAddress.Parse("172.16.0.42");
            // The socket used for client-to-silo will bind to this endpoint
            options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 40000);
            // The socket used by the gateway will bind to this endpoint
            options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 50000);
        });
    }
    // </endpoint_options>

    // <application_parts>
    public static void ConfigureApplicationParts(ISiloHostBuilder siloBuilder)
    {
        siloBuilder.ConfigureApplicationParts(
            parts => parts.AddApplicationPart(
                typeof(ValueGrain).Assembly)
                .WithReferences());
    }
    // </application_parts>
}
