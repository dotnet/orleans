using Infrastructure;
using Orleans;
using Orleans.Configuration;
using Orleans.Dashboard;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
var tableServiceClient = AzureTableServiceClientFactory.Create(builder.Configuration, builder.Environment);
var clusterId = AzureTableServiceClientFactory.GetRequiredValue(builder.Configuration, "Orleans:ClusterId");
var serviceId = AzureTableServiceClientFactory.GetRequiredValue(builder.Configuration, "Orleans:ServiceId");
var clusteringTableName = AzureTableServiceClientFactory.GetRequiredValue(
    builder.Configuration,
    "Orleans:ClusteringTableName");

builder.Services.AddWebAppApplicationInsights("Dashboard");
builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = clusterId;
            options.ServiceId = serviceId;
        })
        .Configure<SiloOptions>(options =>
        {
            options.SiloName = builder.Configuration["Orleans:SiloName"] ?? $"Dashboard-{Environment.MachineName}";
        })
        .ConfigureSampleEndpoints(builder.Configuration, builder.Environment, 11_112, 30_001)
        .UseAzureStorageClustering(options =>
        {
            options.TableServiceClient = tableServiceClient;
            options.TableName = clusteringTableName;
        })
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
app.MapGet("/health/startup", () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok());
app.MapGet("/health/live", () => Results.Ok());

await app.RunAsync();
