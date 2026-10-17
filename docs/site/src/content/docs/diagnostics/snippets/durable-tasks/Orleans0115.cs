namespace Documentation.Diagnostics.DurableTasks;

#pragma warning disable ORLEANS0115
// <orleans0115_violation>
public sealed class ExportGrain(ILogger<ExportGrain> logger) : DurableGrain
{
    public async DurableTask<int> ExportAsync(string[] partitions)
    {
        foreach (var partition in partitions)
        {
            // Logged again for every completed partition each time the task replays.
            logger.LogInformation("Exporting partition {Partition}.", partition);
            await Export(partition).WithId("partition");
        }

        return partitions.Length;
    }

    private static async DurableTask<bool> Export(string partition) => partition.Length > 0;
}
// </orleans0115_violation>
#pragma warning restore ORLEANS0115

// <orleans0115_fix>
public sealed class ReplaySafeExportGrain(ILogger<ReplaySafeExportGrain> logger) : DurableGrain
{
    // Skips events while a task replays, so each is logged once. Ordinary grain methods can use the same logger.
    private readonly ILogger _logger = logger.ReplaySafe();

    public async DurableTask<int> ExportAsync(string[] partitions)
    {
        foreach (var partition in partitions)
        {
            _logger.LogInformation("Exporting partition {Partition}.", partition);
            await Export(partition).WithId("partition");
        }

        return partitions.Length;
    }

    private static async DurableTask<bool> Export(string partition) => partition.Length > 0;
}
// </orleans0115_fix>
