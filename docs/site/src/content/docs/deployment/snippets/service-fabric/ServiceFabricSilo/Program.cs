using System.Fabric;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans.Configuration;
using Orleans.Hosting;

namespace ServiceFabricSilo;

internal static class Program
{
    private const string ServiceTypeName = "OrleansSiloType";

    public static async Task Main()
    {
        try
        {
            await ServiceRuntime.RegisterServiceAsync(
                ServiceTypeName,
                context => new OrleansStatelessService(context, CreateHost));

            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            throw;
        }
    }

    private static IHost CreateHost(StatelessServiceContext context)
    {
        var activationContext = context.CodePackageActivationContext;
        var siloEndpoint = activationContext.GetEndpoint("OrleansSiloEndpoint");
        var gatewayEndpoint = activationContext.GetEndpoint("OrleansGatewayEndpoint");
        var advertisedHost = context.NodeContext.IPAddressOrFQDN;

        var serviceId = GetRequiredSetting("ORLEANS_SERVICE_ID");
        var clusterId = GetRequiredSetting("ORLEANS_CLUSTER_ID");
        var tableServiceUri = new Uri(GetRequiredSetting("ORLEANS_TABLE_SERVICE_URI"));

        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .Configure<ClusterOptions>(options =>
                {
                    options.ServiceId = serviceId;
                    options.ClusterId = clusterId;
                })
                .UseAzureStorageClustering(options =>
                    options.TableServiceClient = new TableServiceClient(
                        tableServiceUri,
                        new DefaultAzureCredential()))
                .ConfigureEndpoints(
                    advertisedHost,
                    siloEndpoint.Port,
                    gatewayEndpoint.Port,
                    listenOnAnyHostAddress: true);
        });

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(120);
        });

        return builder.Build();
    }

    private static string GetRequiredSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"The required setting '{name}' isn't configured.");
}
