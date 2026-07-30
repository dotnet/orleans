using Azure.Data.Tables;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Scaler.Services;

var builder = WebApplication.CreateBuilder(args);
var storageConnectionString = builder.Configuration["StorageConnectionString"]
    ?? throw new InvalidOperationException("StorageConnectionString is not configured.");

builder.Logging.SetMinimumLevel(LogLevel.Warning).AddJsonConsole();
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "Cluster";
            options.ServiceId = "Service";
        })
        .UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(storageConnectionString));
});

builder.Services.AddGrpc();
builder.Services.AddWebAppApplicationInsights("Scaler");

var app = builder.Build();

app.MapGrpcService<ExternalScalerService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();