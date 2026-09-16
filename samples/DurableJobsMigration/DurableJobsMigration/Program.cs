using Azure.Storage.Blobs;
using DurableJobsMigration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Serialization;

var builder = Host.CreateApplicationBuilder(args);
var phase = builder.Configuration["Migration:Phase"]
    ?? throw new InvalidOperationException("Run the AppHost, or set Migration:Phase to prepare or drain.");
if (phase is not ("prepare" or "drain"))
{
    throw new InvalidOperationException("Migration:Phase must be prepare or drain.");
}

var runId = builder.Configuration["Migration:RunId"]
    ?? throw new InvalidOperationException("Migration:RunId must be the same GUID for both phases.");
if (!Guid.TryParseExact(runId, "N", out _))
{
    throw new InvalidOperationException("Migration:RunId must be a GUID formatted without separators.");
}

var blobClient = new BlobServiceClient(builder.Configuration.GetConnectionString("blobs")
    ?? throw new InvalidOperationException("Run the AppHost to configure Azurite."));
var control = blobClient.GetBlobContainerClient($"migration-control-{runId}");
await control.CreateIfNotExistsAsync();
var run = new MigrationRun(phase, control);
builder.Services.AddSingleton(run);
builder.AddKeyedAzureTableServiceClient("clustering");

builder.UseOrleans(silo =>
{
    silo.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = $"migration-{runId}";
        options.ServiceId = "durable-jobs-migration";
    });
    silo.AddAzureBlobJournalStorage("jobs-a", options =>
    {
        options.BlobServiceClient = blobClient;
        options.ContainerName = $"migration-a-{runId}";
    });
    silo.AddAzureBlobJournalStorage("jobs-b", options =>
    {
        options.BlobServiceClient = blobClient;
        options.ContainerName = $"migration-b-{runId}";
    });
    silo.UseJournaledDurableJobs(options =>
    {
        options.WriteProviderName = phase == "prepare" ? "jobs-a" : "jobs-b";
        options.DrainingProviderNames.Add(phase == "prepare" ? "jobs-b" : "jobs-a");
        options.ShardDuration = TimeSpan.FromSeconds(5);
        options.ShardCheckInterval = TimeSpan.FromSeconds(1);
        options.ShardClaimRampUpDuration = TimeSpan.Zero;
        options.MaxConcurrentJobsPerSilo = 4;
        options.ConcurrencySlowStartEnabled = false;
        options.JobStatusPollInterval = TimeSpan.FromMilliseconds(100);
        // Load future shards in the drain phase so saved handles can cancel
        // owned jobs while their executors await the scheduled start window.
        options.ShardLoadLookaheadPeriod = phase == "prepare" ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2);
        options.ShardActivationBufferPeriod = options.ShardLoadLookaheadPeriod;
        options.ShouldRetry = (context, _) => context.DequeueCount < 3 ? DateTimeOffset.UtcNow.AddSeconds(5) : null;
    });
});

using var host = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var cancellationToken = timeout.Token;
await host.StartAsync(cancellationToken);
try
{
    var client = host.Services.GetRequiredService<IClusterClient>();
    var grain = client.GetGrain<IMigrationGrain>(runId);
    var inspector = host.Services.GetRequiredService<IDurableJobsStorageInspector>();
    var serializer = host.Services.GetRequiredService<Serializer>();
    var manifestBlob = control.GetBlobClient("manifest");

    if (phase == "prepare")
    {
        var dueDelay = builder.Configuration.GetValue("Migration:DueDelaySeconds", 30);
        if (dueDelay < 10 || dueDelay > 90)
        {
            throw new InvalidOperationException("Migration:DueDelaySeconds must be between 10 and 90.");
        }

        var existing = await grain.ScheduleAsync("old-work", DateTimeOffset.UtcNow.AddSeconds(dueDelay));
        var future = await grain.ScheduleAsync("future-cancel", existing.DueTime.AddSeconds(30));
        var manifest = new MigrationManifest(existing, future);
        await manifestBlob.UploadAsync(BinaryData.FromBytes(serializer.SerializeToArray(manifest)),
            overwrite: false, cancellationToken: cancellationToken);

        var a = await PrintInventoryAsync(inspector, "jobs-a", cancellationToken);
        var b = await PrintInventoryAsync(inspector, "jobs-b", cancellationToken);
        Require(a.ShardCount == 2 && a.NewestShardStartTime > DateTimeOffset.UtcNow.AddSeconds(5),
            "Full A inventory must include both shards, beyond the prepare host's lookahead.");
        Require(IsEmpty(b), "B must be empty before cutover.");
        Require(!(await run.ReceiptBlob(existing).ExistsAsync(cancellationToken)).Value,
            "A work ran before shutdown; increase Migration:DueDelaySeconds and use a new run.");
        Console.WriteLine($"PREPARED: handles saved in {control.Name}/manifest. Releasing A and exiting.");
    }
    else
    {
        var stored = await manifestBlob.DownloadContentAsync(cancellationToken);
        var manifest = serializer.Deserialize<MigrationManifest>(stored.Value.Content.ToArray())
            ?? throw new InvalidOperationException("The stored migration manifest is empty.");
        var aBefore = await PrintInventoryAsync(inspector, "jobs-a", cancellationToken);
        Require(aBefore.ShardCount > 0, "A's persisted shards must survive the prepare process.");
        var current = await grain.ScheduleAsync("new-work", DateTimeOffset.UtcNow.AddSeconds(10));
        var bBefore = await PrintInventoryAsync(inspector, "jobs-b", cancellationToken);
        Require(bBefore.ShardCount > 0 && bBefore.IsWriteProvider, "New B work must create a B shard.");
        Require(current.ShardId != manifest.Existing.ShardId && current.ShardId != manifest.Future.ShardId,
            "New work must have its own unchanged-format shard identity.");

        // Wait for discovery/ownership, not for the job's due time.
        var jobs = host.Services.GetRequiredService<ILocalDurableJobManager>();
        while (!await jobs.CancelAsync(manifest.Future, cancellationToken))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        Console.WriteLine($"Canceled the saved A handle: {manifest.Future.Id}, shard {manifest.Future.ShardId}");
        await VerifyReceiptAsync(run, manifest.Existing, minimumAttempts: 2, cancellationToken);
        await VerifyReceiptAsync(run, current, minimumAttempts: 1, cancellationToken);
        Require(!(await run.ReceiptBlob(manifest.Future).ExistsAsync(cancellationToken)).Value,
            "The canceled future job must not have executed.");

        while (true)
        {
            var a = await PrintInventoryAsync(inspector, "jobs-a", cancellationToken);
            var b = await PrintInventoryAsync(inspector, "jobs-b", cancellationToken);
            if (IsEmpty(a) && IsEmpty(b))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        Console.WriteLine("VERIFIED: A recovered and retried in its original shard; B ran new work; saved-handle cancellation and complete A/B cleanup succeeded.");
        Console.WriteLine("This isolated run has completed its writer cutover and confirmed empty A/B inventories.");
    }
}
finally
{
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await host.StopAsync(shutdown.Token);
}

static async Task<DurableJobsStorageStatus> PrintInventoryAsync(
    IDurableJobsStorageInspector inspector, string provider, CancellationToken cancellationToken)
{
    try
    {
        var status = await inspector.InspectAsync(provider, cancellationToken);
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} {status.ProviderName}: write={status.IsWriteProvider}, shards={status.ShardCount}, owned={status.OwnedShardCount}, poisoned={status.PoisonedShardCount}, unrecognized={status.UnrecognizedShardCount}, oldest={status.OldestShardStartTime:O}, newest={status.NewestShardStartTime:O}");
        return status;
    }
    catch
    {
        Console.Error.WriteLine($"{provider}: UNKNOWN (inspection failed; no complete snapshot)");
        throw;
    }
}

static bool IsEmpty(DurableJobsStorageStatus status) => status.ShardCount == 0;

static async Task VerifyReceiptAsync(MigrationRun run, DurableJob job, int minimumAttempts, CancellationToken cancellationToken)
{
    var receipt = run.ReceiptBlob(job);
    while (!(await receipt.ExistsAsync(cancellationToken)).Value)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    var content = (await receipt.DownloadContentAsync(cancellationToken)).Value.Content.ToString().Split('|');
    Require(content.Length == 3 && content[0] == "drain" && content[1] == job.ShardId
        && int.TryParse(content[2], out var attempts) && attempts >= minimumAttempts,
        $"Job {job.Id} must execute in the drain process, retain its original shard, and meet its expected attempt count.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
