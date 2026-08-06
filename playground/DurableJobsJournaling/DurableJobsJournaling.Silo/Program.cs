using Azure.Storage.Blobs;
using DurableJobsJournaling.Silo;
using Orleans.Dashboard;
using Orleans.Journaling;
using Orleans.Journaling.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureBlobServiceClient("blobs");
builder.AddKeyedAzureTableServiceClient("tables");

var storageContainer = builder.Configuration.GetValue("Playground:Storage:Container", "durablejobs-journaling-playground");
var storagePrefix = builder.Configuration.GetValue("Playground:Storage:Prefix", $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");

builder.UseOrleans(siloBuilder =>
{
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    siloBuilder
        .AddDashboard()
        .AddActivityPropagation()
        .AddIncomingGrainCallFilter<GrainRequestMetricsFilter>()
        .AddDistributedGrainDirectory()
        .UseAzureBlobDurableJobs(
            options =>
            {
                options.ContainerName = storageContainer;
                options.GetWalBlobName = journalId => $"{storagePrefix}/{journalId.Value}/wal";
                options.GetCheckpointBlobName = (journalId, snapshotId) => $"{storagePrefix}/{journalId.Value}/chk.{snapshotId}";
            })
        .UseJsonJournalFormat(DurableJobsJournalingJsonContext.Default)
        .Configure<DurableJobsOptions>(options =>
        {
            options.ShardDuration = TimeSpan.FromMinutes(2);
            options.ShardActivationBufferPeriod = TimeSpan.FromSeconds(30);
            options.ShardStripeCount = 2;
            options.JobStatusPollInterval = TimeSpan.FromMilliseconds(100);
            options.MaxConcurrentJobsPerSilo = 8196;
            options.ConcurrencySlowStartEnabled = true;
            options.SlowStartInterval = TimeSpan.FromSeconds(2);
            builder.Configuration.GetSection("Playground:DurableJobs").Bind(options);
            options.ShouldRetry = (context, _) => context.DequeueCount < 3
                ? DateTimeOffset.UtcNow.AddMilliseconds(250 * Math.Pow(2, context.DequeueCount))
                : null;
        });
#pragma warning restore ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
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
