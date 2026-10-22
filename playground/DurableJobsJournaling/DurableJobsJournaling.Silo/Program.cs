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
    foreach (var name in new[] { "jobs-a", "jobs-b" })
    {
        builder.Services.AddOptions<AzureTableJournalStorageOptions>(name)
            .Configure<TableServiceClient>((options, client) => options.TableServiceClient = client);
    }
}
else
{
    builder.AddAzureBlobServiceClient("blobs");
    foreach (var name in new[] { "jobs-a", "jobs-b" })
    {
        builder.Services.AddOptions<AzureBlobJournalStorageOptions>(name)
            .Configure<BlobServiceClient>((options, client) => options.BlobServiceClient = client);
    }
}

builder.Services.AddHostedService<StorageInventoryReporter>();

builder.UseOrleans(siloBuilder =>
{
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    if (backend.UsesTableJournal())
    {
        siloBuilder.AddAzureTableJournalStorage("jobs-a", options =>
        {
            options.TableName = builder.Configuration["Playground:Storage:Table"]
                ?? throw new InvalidOperationException("Set Playground:Storage:Table to the shared run-specific table name.");
        });
        siloBuilder.AddAzureTableJournalStorage("jobs-b", options =>
        {
            options.TableName = builder.Configuration["Playground:Storage:TableB"]
                ?? throw new InvalidOperationException("Set Playground:Storage:TableB to the second shared table name.");
        });
    }
    else
    {
        siloBuilder.AddAzureBlobJournalStorage("jobs-a", options =>
        {
            options.ContainerName = builder.Configuration["Playground:Storage:Container"]
                ?? throw new InvalidOperationException("Set Playground:Storage:Container to the shared run-specific container name.");
        });
        siloBuilder.AddAzureBlobJournalStorage("jobs-b", options =>
        {
            options.ContainerName = builder.Configuration["Playground:Storage:ContainerB"]
                ?? throw new InvalidOperationException("Set Playground:Storage:ContainerB to the second shared container name.");
        });
    }

    siloBuilder
        .UseJournaledDurableJobs(options =>
        {
            options.ActiveProviderName = builder.Configuration.GetValue("Playground:Migration:ActiveProviderName", "jobs-a")!;
            if (builder.Configuration.GetValue("Playground:Migration:DrainOtherProvider", true))
            {
                options.DrainingProviderNames.Add(options.ActiveProviderName == "jobs-a" ? "jobs-b" : "jobs-a");
            }
        })
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
