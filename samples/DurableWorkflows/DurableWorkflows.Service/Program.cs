using Azure.Storage.Blobs;
using DurableWorkflows;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;

var builder = WebApplication.CreateBuilder(args);

builder.AddKeyedRedisClient("clustering");
builder.AddAzureBlobServiceClient("durable-state");
builder.Services.AddHealthChecks()
    .AddCheck("self", static () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), ["live"]);

builder.UseOrleans(siloBuilder =>
{
    // Grain journals and Durable Jobs share the Azure Blob journal provider, so every replica recovers the same task
    // state and delays. The Orleans binary journal format stores the sample's [GenerateSerializer] state types.
    siloBuilder
        .AddJournaling()
        .UseAzureBlobDurableJobs(options => options.ContainerName = "durable-workflows")
        .Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary")
        .AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(24));
});

builder.Services.AddOptions<AzureBlobJournalStorageOptions>()
    .Configure<BlobServiceClient>((options, blobServiceClient) => options.BlobServiceClient = blobServiceClient);

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});

app.MapDurableWorkflowEndpoints();

await app.RunAsync();
