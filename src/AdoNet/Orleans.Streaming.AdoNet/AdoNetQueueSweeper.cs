using System.Threading;
using Microsoft.Extensions.Hosting;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Performs background maintenance tasks for the associated AdoNet stream provider.
/// The tasks are segregated by individual queue to lessen contention on the database.
/// The schedule is scaled dynamically according to the number of silos in the cluster to avoid database stampedes.
/// </summary>
/// <remarks>
/// This class is modelled as a background service for ease of implementation.
/// However this service does not share a lifetime with the host due to being provider specific.
/// At the time of development, keyed hosted services are not yet supported, so the lifetime is managed by the creator.
/// </remarks>
internal sealed partial class AdoNetQueueSweeper(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, AdoNetStreamQueueMapper mapper, RelationalOrleansQueries queries, ISiloStatusOracle oracle, ILogger<AdoNetQueueSweeper> logger) : BackgroundService
{
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Allows the caller to wait for the first cycle if it desires.
    /// </summary>
    private readonly TaskCompletionSource _started = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // clean up dead letters for each queue if enabled
                    if (streamOptions.RemoveDeadLetters)
                    {
                        foreach (var queueId in mapper.GetAllAdoNetQueueIds())
                        {
                            int affected;
                            do
                            {
                                affected = await queries
                                    .CleanStreamDeadLettersAsync(clusterOptions.ServiceId, name, queueId, streamOptions.SweepBatchSize)
                                    .WaitAsync(stoppingToken);
                            }
                            while (affected >= streamOptions.SweepBatchSize);
                        }
                    }

                    // clean up messages for each queue
                    foreach (var queueId in mapper.GetAllAdoNetQueueIds())
                    {
                        int affected;
                        do
                        {
                            affected = await queries
                                .CleanStreamMessagesAsync(clusterOptions.ServiceId, name, queueId, streamOptions.SweepBatchSize, streamOptions.MaxAttempts, streamOptions.RemovalTimeout.TotalSecondsCeiling())
                                .WaitAsync(stoppingToken);
                        }
                        while (affected >= streamOptions.SweepBatchSize);
                    }

                    // propagate first cycle success
                    _started.TrySetResult();

                    // get the current silo count in the cluster so we can scale the schedule
                    var siloCount = oracle.GetApproximateSiloStatuses(true).Count;
                    if (siloCount <= 0)
                    {
                        LogUnexpectedSiloCount(siloCount);
                        await Task.Delay(streamOptions.SweepPeriod, stoppingToken);
                        continue;
                    }

                    // scale the schedule based on the above
                    var period = streamOptions.SweepPeriod.Multiply(siloCount);

                    // randomize the period to reduce stampede probability
                    var random = TimeSpan.FromMilliseconds(period.TotalMilliseconds * (Random.Shared.NextDouble() - 0.5));
                    period += random;

                    // now we can wait for the next cycle
                    await Task.Delay(period, stoppingToken);
                }
                catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
                {
                    // the host is shutting down gracefully and so will we
                    _started.TrySetException(ex);
                    LogMaintenanceGracefullyStopping(clusterOptions.ServiceId, name);
                    break;
                }
                catch (Exception ex)
                {
                    // maintenance work failed
                    _started.TrySetException(ex);
                    LogTransientlyFailedMaintenance(ex, clusterOptions.ServiceId, name);
                }
            }
        }
        catch (Exception ex)
        {
            // we should never get here even when faults happen
            _started.TrySetException(ex);
            LogTerminallyFailedMaintenance(ex, clusterOptions.ServiceId, name);
            throw;
        }
    }

    /// <summary>
    /// Gets a task that completes when the first maintenance cycle completes.
    /// Exceptions from the first cycle will be bubbled but not stop the following cycles from running.
    /// </summary>
    /// <remarks>
    /// This is designed to support unit testing.
    /// </remarks>
    public Task Started => _started.Task;

    #region Logging

    [LoggerMessage(1, LogLevel.Warning, "Oracle returned unexpected silo count {SiloCount}. Skipping maintenance until stability is regained.")]
    private partial void LogUnexpectedSiloCount(int siloCount);

    [LoggerMessage(2, LogLevel.Error, "Failed to perform maintenance for ({ServiceId}, {ProviderId}) and will try again later")]
    private partial void LogTransientlyFailedMaintenance(Exception ex, string serviceId, string providerId);

    [LoggerMessage(3, LogLevel.Error, "Terminally failed to perform maintenance for ({ServiceId}, {ProviderId}) and will not try again")]
    private partial void LogTerminallyFailedMaintenance(Exception ex, string serviceId, string providerId);

    [LoggerMessage(4, LogLevel.Information, "Maintenance schedule for ({ServiceId}, {ProviderId}) is gracefully stopping")]
    private partial void LogMaintenanceGracefullyStopping(string serviceId, string providerId);

    #endregion Logging
}