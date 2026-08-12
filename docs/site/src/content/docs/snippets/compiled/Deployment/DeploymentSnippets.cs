using System.Net;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Documentation.Deployment.AppService
{
    internal static class AppServiceSnippets
    {
        internal static void ConfigureEndpoints(
            ISiloBuilder siloBuilder,
            IPAddress privateIp,
            int siloPort)
        {
            // <configure_app_service_endpoints>
siloBuilder.ConfigureEndpoints(
    privateIp,
    siloPort,
    gatewayPort: 0,
    listenOnAnyHostAddress: true);
            // </configure_app_service_endpoints>
        }
    }
}

namespace Documentation.Deployment.ContainerApps.Endpoints
{
    // <container_apps_endpoint_usings>
using System.Net;
using Orleans.Configuration;

    // </container_apps_endpoint_usings>

    internal static class ContainerAppsEndpointSnippets
    {
        internal static void Configure(WebApplicationBuilder builder)
        {
            // <configure_container_apps_endpoints>
var advertisedAddress = IPAddress.Parse(
    builder.Configuration["ORLEANS_ADVERTISED_IP"]
        ?? throw new InvalidOperationException("ORLEANS_ADVERTISED_IP isn't configured."));
var advertisedSiloPort = int.Parse(
    builder.Configuration["ORLEANS_ADVERTISED_SILO_PORT"]
        ?? throw new InvalidOperationException("ORLEANS_ADVERTISED_SILO_PORT isn't configured."));
var advertisedGatewayPort = int.Parse(
    builder.Configuration["ORLEANS_ADVERTISED_GATEWAY_PORT"]
        ?? throw new InvalidOperationException("ORLEANS_ADVERTISED_GATEWAY_PORT isn't configured."));

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<EndpointOptions>(options =>
    {
        options.AdvertisedIPAddress = advertisedAddress;
        options.SiloPort = advertisedSiloPort;
        options.GatewayPort = advertisedGatewayPort;
        options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 11_111);
        options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 30_000);
    });
});
            // </configure_container_apps_endpoints>
        }
    }
}

namespace Documentation.Deployment.ContainerApps.Storage
{
    // <container_apps_storage_usings>
using Azure.Data.Tables;
using Azure.Identity;
using Orleans.Configuration;

    // </container_apps_storage_usings>

    internal static class ContainerAppsStorageSnippets
    {
        internal static void Configure(WebApplicationBuilder builder)
        {
            // <configure_container_apps_storage>
var tableEndpoint = new Uri(
    builder.Configuration["AZURE_TABLE_STORAGE_ENDPOINT"]
        ?? throw new InvalidOperationException("AZURE_TABLE_STORAGE_ENDPOINT isn't configured."));
var tableServiceClient = new TableServiceClient(
    tableEndpoint,
    new DefaultAzureCredential());

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "orders";
            options.ClusterId = builder.Configuration["ORLEANS_CLUSTER_ID"]
                ?? throw new InvalidOperationException("ORLEANS_CLUSTER_ID isn't configured.");
        })
        .UseAzureStorageClustering(
            options => options.TableServiceClient = tableServiceClient)
        .AddAzureTableGrainStorage(
            name: "default",
            options => options.TableServiceClient = tableServiceClient);
});
            // </configure_container_apps_storage>
        }
    }
}

namespace Documentation.Deployment.Kubernetes
{
    // <kubernetes_usings>
using System.Net;

    // </kubernetes_usings>

    internal static class KubernetesSnippets
    {
        internal static void Configure(string[] args)
        {
            // <configure_kubernetes_silo>
var builder = WebApplication.CreateBuilder(args);

var podName = builder.Configuration["POD_NAME"]
    ?? throw new InvalidOperationException("POD_NAME isn't configured.");
var podIp = IPAddress.Parse(
    builder.Configuration["POD_IP"]
        ?? throw new InvalidOperationException("POD_IP isn't configured."));

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        // Configure one production clustering provider here.
        .Configure<ClusterOptions>(options =>
        {
            options.ServiceId = builder.Configuration["ORLEANS_SERVICE_ID"]
                ?? throw new InvalidOperationException("ORLEANS_SERVICE_ID isn't configured.");
            options.ClusterId = builder.Configuration["ORLEANS_CLUSTER_ID"]
                ?? throw new InvalidOperationException("ORLEANS_CLUSTER_ID isn't configured.");
        })
        .Configure<SiloOptions>(options => options.SiloName = podName)
        .ConfigureEndpoints(
            advertisedIP: podIp,
            siloPort: 11_111,
            gatewayPort: 30_000,
            listenOnAnyHostAddress: true);
});

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(120);
});

var app = builder.Build();

// Map application-owned startup, readiness, and liveness endpoints.

app.Run();
            // </configure_kubernetes_silo>
        }
    }
}
