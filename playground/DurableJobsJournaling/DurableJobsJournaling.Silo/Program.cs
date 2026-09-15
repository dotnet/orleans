using Azure.Data.Tables;
using Azure.Storage.Blobs;
using DurableJobsJournaling;
using DurableJobsJournaling.Silo;
using Orleans.Dashboard;
using Orleans.Journaling;
using Orleans.Journaling.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("tables");

var backend = StorageBackendConfiguration.Parse(builder.Configuration.GetValue("Playground:Storage:Provider", "Azurite"));
if (backend.UsesTableJournal())
{
    builder.AddAzureTableServiceClient("journals");
    builder.Services.AddOptions<AzureTableJournalStorageOptions>()
        .Configure<TableServiceClient>((options, client) =>
        {
            options.TableServiceClient = client;
        });
}
else
{
    builder.AddAzureBlobServiceClient("blobs");
    builder.Services.AddOptions<AzureBlobJournalStorageOptions>()
        .Configure<BlobServiceClient>((options, client) =>
        {
            options.BlobServiceClient = client;
        });
}

builder.UseOrleans(siloBuilder =>
{
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    if (backend.UsesTableJournal())
    {
        siloBuilder.UseAzureTableDurableJobs(options =>
        {
            options.TableName = builder.Configuration["Playground:Storage:Table"]
                ?? throw new InvalidOperationException("Set Playground:Storage:Table to the shared run-specific table name.");
        });
    }
    else
    {
        siloBuilder.UseAzureBlobDurableJobs(options =>
        {
            options.ContainerName = builder.Configuration["Playground:Storage:Container"]
                ?? throw new InvalidOperationException("Set Playground:Storage:Container to the shared run-specific container name.");
        });
    }

    siloBuilder
        .AddDashboard()
        .AddActivityPropagation()
        .AddIncomingGrainCallFilter<GrainRequestMetricsFilter>()
        .AddDistributedGrainDirectory()
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

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapOrleansDashboard();

await app.RunAsync();
