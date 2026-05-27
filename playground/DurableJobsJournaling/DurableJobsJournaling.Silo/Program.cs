using Azure.Storage.Blobs;
using DurableJobsJournaling.Abstractions;
using DurableJobsJournaling.Silo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Dashboard;
using Orleans.Hosting;
using Orleans.Journaling;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");
builder.AddKeyedAzureTableServiceClient("tables");

var storageContainer = builder.Configuration.GetValue("Playground:Storage:Container", "durablejobs-journaling-playground");
var storagePrefix = builder.Configuration.GetValue("Playground:Storage:Prefix", $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .AddActivityPropagation()
        .AddIncomingGrainCallFilter<GrainRequestMetricsFilter>()
        .AddAzureBlobJournalStorage()
        .UseAzureBlobDurableJobs(
            options =>
            {
                options.ContainerName = storageContainer;
                options.GetBlobName = journalId => $"{storagePrefix}/{journalId.Value}";
            },
            DurableJobsJournalingJsonContext.Default)
        .Configure<DurableJobsOptions>(options =>
        {
            options.ShardDuration = TimeSpan.FromMinutes(5);
            options.ShardActivationBufferPeriod = TimeSpan.FromSeconds(30);
            options.ShardStripeCount = 1;
            options.JobStatusPollInterval = TimeSpan.FromMilliseconds(100);
            options.MaxConcurrentJobsPerSilo = 512;
            options.ConcurrencySlowStartEnabled = true;
            options.SlowStartInitialConcurrency = 16;
            options.SlowStartInterval = TimeSpan.FromSeconds(5);
            builder.Configuration.GetSection("Playground:DurableJobs").Bind(options);
            options.ShouldRetry = (context, _) => context.DequeueCount < 3
                ? DateTimeOffset.UtcNow.AddMilliseconds(250 * Math.Pow(2, context.DequeueCount))
                : null;
        })
        .AddDashboard();
});

builder.Services.AddOptions<AzureBlobJournalStorageOptions>()
    .Configure<BlobServiceClient>((options, blobServiceClient) =>
    {
        options.BlobServiceClient = blobServiceClient;
    });

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapOrleansDashboard();

await app.RunAsync();
