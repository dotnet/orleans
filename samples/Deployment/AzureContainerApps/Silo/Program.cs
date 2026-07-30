using Azure.Data.Tables;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
var storageConnectionString = builder.Configuration["StorageConnectionString"]
    ?? throw new InvalidOperationException("StorageConnectionString is not configured.");

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "Cluster";
            options.ServiceId = "Service";
        })
        .Configure<SiloOptions>(options =>
        {
            options.SiloName = "Silo";
        })
        .ConfigureEndpoints(siloPort: 11_111, gatewayPort: 30_000)
        .UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(storageConnectionString));
});

builder.Services.AddWebAppApplicationInsights("Silo");

// uncomment this if you dont mind hosting grains in the dashboard
builder.Services.DontHostGrainsHere();

var app = builder.Build();

app.MapGet("/", () => Results.Ok("Silo"));

app.Run();
