using Infrastructure;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Scaler.Services;

var builder = WebApplication.CreateBuilder(args);
var tableServiceClient = AzureTableServiceClientFactory.Create(builder.Configuration, builder.Environment);
var clusterId = AzureTableServiceClientFactory.GetRequiredValue(builder.Configuration, "Orleans:ClusterId");
var serviceId = AzureTableServiceClientFactory.GetRequiredValue(builder.Configuration, "Orleans:ServiceId");
var clusteringTableName = AzureTableServiceClientFactory.GetRequiredValue(
    builder.Configuration,
    "Orleans:ClusteringTableName");

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

builder.Services.AddGrpc();
builder.Services.AddWebAppApplicationInsights("Scaler");

var app = builder.Build();

app.MapGrpcService<ExternalScalerService>();
app.MapGet("/", () => "The external scaler gRPC endpoint is running.");
app.MapGet("/health/startup", () => Results.Ok());
app.MapGet("/health/ready", () => Results.Ok());
app.MapGet("/health/live", () => Results.Ok());

await app.RunAsync();