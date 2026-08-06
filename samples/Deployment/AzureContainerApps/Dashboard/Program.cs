using Azure.Data.Tables;
using Orleans;
using Orleans.Configuration;
using Orleans.Dashboard;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
var storageConnectionString = builder.Configuration["StorageConnectionString"]
    ?? throw new InvalidOperationException("StorageConnectionString is not configured.");

builder.Services.AddWebAppApplicationInsights("Dashboard");
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
            options.SiloName = "Dashboard";
        })
        .ConfigureEndpoints(siloPort: 11_112, gatewayPort: 30_001)
        .UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(storageConnectionString))
        .AddDashboard(config =>
            config.HideTrace =
                !string.IsNullOrEmpty(builder.Configuration.GetValue<string>("HideTrace"))
                    ? builder.Configuration.GetValue<bool>("HideTrace")
                    : true);
});

// uncomment this if you dont mind hosting grains in the dashboard
builder.Services.DontHostGrainsHere();

var app = builder.Build();

app.MapOrleansDashboard();
app.MapGet("/health", () => Results.Ok("Dashboard"));

app.Run();
