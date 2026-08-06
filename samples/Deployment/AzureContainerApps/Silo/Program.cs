using Infrastructure;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);
var tableServiceClient = AzureTableServiceClientFactory.Create(builder.Configuration, builder.Environment);
var clusterId = AzureTableServiceClientFactory.GetRequiredValue(builder.Configuration, "Orleans:ClusterId");
var serviceId = AzureTableServiceClientFactory.GetRequiredValue(builder.Configuration, "Orleans:ServiceId");
var clusteringTableName = AzureTableServiceClientFactory.GetRequiredValue(
    builder.Configuration,
    "Orleans:ClusteringTableName");

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
            options.SiloName = builder.Configuration["Orleans:SiloName"] ?? $"Silo-{Environment.MachineName}";
        })
        .ConfigureSampleEndpoints(builder.Configuration, builder.Environment, 11_111, 30_000)
        .UseAzureStorageClustering(options =>
        {
            options.TableServiceClient = tableServiceClient;
            options.TableName = clusteringTableName;
        });
});

builder.Services.AddWebAppApplicationInsights("Silo");

builder.Services.DontHostGrainsHere();

var app = builder.Build();

app.MapGet("/", () => Results.Ok("Silo"));
app.MapGet("/health/startup", () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok());
app.MapGet("/health/live", () => Results.Ok());

await app.RunAsync();
