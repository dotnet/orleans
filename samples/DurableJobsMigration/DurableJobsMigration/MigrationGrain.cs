using Azure.Storage.Blobs;
using Orleans;
using Orleans.DurableJobs;

namespace DurableJobsMigration;

public interface IMigrationGrain : IGrainWithStringKey
{
    Task<DurableJob> ScheduleAsync(string name, DateTimeOffset dueTime);
}

public sealed class MigrationGrain(ILocalDurableJobManager jobs, MigrationRun run) : Grain, IMigrationGrain, IDurableJobHandler
{
    public Task<DurableJob> ScheduleAsync(string name, DateTimeOffset dueTime) =>
        jobs.ScheduleJobAsync(new ScheduleJobRequest
        {
            Target = this.GetGrainId(),
            JobName = name,
            DueTime = dueTime
        }, CancellationToken.None);

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        if (context.Job.Name == "old-work" && context.DequeueCount == 1)
        {
            throw new InvalidOperationException("One intentional failure demonstrates retry in the original A shard.");
        }

        // Overwriting the same receipt makes the sample's side effect idempotent.
        // Receipts are independent of job journals and survive process restarts.
        var receipt = $"{run.Phase}|{context.Job.ShardId}|{context.DequeueCount}";
        await run.ReceiptBlob(context.Job).UploadAsync(BinaryData.FromString(receipt),
            overwrite: true, cancellationToken: attemptCancellationToken);
        Console.WriteLine($"Executed {context.Job.Name}: {receipt}");
    }
}

public sealed record MigrationRun(string Phase, BlobContainerClient Control)
{
    public BlobClient ReceiptBlob(DurableJob job) => Control.GetBlobClient($"receipts/{job.Id}");
}

[GenerateSerializer]
public sealed record MigrationManifest(
    [property: Id(0)] DurableJob Existing,
    [property: Id(1)] DurableJob Future);
