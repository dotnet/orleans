using Clients.WorkerService;
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

builder.Services.AddWorkerAppApplicationInsights("Worker Service Client");
builder.Services.AddHostedService<Worker>();
builder.Logging.SetMinimumLevel(LogLevel.Warning).AddJsonConsole();
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = clusterId;
            options.ServiceId = serviceId;
        })
        .UseAzureStorageClustering(options =>
        {
            options.TableServiceClient = tableServiceClient;
            options.TableName = clusteringTableName;
        });
});

var app = builder.Build();
app.MapGet("/health/startup", () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok());
app.MapGet("/health/live", () => Results.Ok());
await app.RunAsync();