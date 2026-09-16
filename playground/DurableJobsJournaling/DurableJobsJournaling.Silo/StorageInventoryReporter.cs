using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Hosting;

namespace DurableJobsJournaling.Silo;

internal sealed class StorageInventoryReporter(
    IDurableJobsStorageInspector inspector,
    IOptions<DurableJobsOptions> options,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<StorageInventoryReporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Playground:Migration:ReportInventory", false))
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);

        var providers = options.Value.DrainingProviderNames.Prepend(options.Value.WriteProviderName).Distinct().ToArray();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            foreach (var provider in providers)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var status = await inspector.InspectAsync(provider, timeout.Token);
                    logger.LogInformation(
                        "Inventory {Provider}: write={IsWriteProvider}, shards={ShardCount}, owned={Owned}, poisoned={Poisoned}, unrecognized={Unrecognized}, oldest={Oldest}, newest={Newest}. Local live snapshot, not a retirement guarantee",
                        status.ProviderName, status.IsWriteProvider, status.ShardCount, status.OwnedShardCount,
                        status.PoisonedShardCount, status.UnrecognizedShardCount, status.OldestShardStartTime, status.NewestShardStartTime);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Inventory {Provider}: UNKNOWN; no successful complete snapshot", provider);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
