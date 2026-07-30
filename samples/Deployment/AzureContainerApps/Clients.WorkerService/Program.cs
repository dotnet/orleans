using Azure.Data.Tables;
using Clients.WorkerService;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var storageConnectionString = builder.Configuration["StorageConnectionString"]
    ?? throw new InvalidOperationException("StorageConnectionString is not configured.");

builder.Services.AddWorkerAppApplicationInsights("Worker Service Client");
builder.Services.AddHostedService<Worker>();
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

var host = builder.Build();
await host.RunAsync();