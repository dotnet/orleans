using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Durable.Jobs;
using Orleans.Placement;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

[KeepAlive]
[PreferLocalPlacement]
internal sealed class AdvancedReminderRecoveryGrain(
    IReminderTable reminderTable,
    IGrainFactory grainFactory,
    ILogger<AdvancedReminderRecoveryGrain> logger,
    IOptions<ReminderOptions> options,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null) : Grain, IAdvancedReminderRecoveryGrain
{
    private const int BatchSize = 32;
    internal const int RecoveryPageSize = 256;
    internal const int MaxPagesPerReconciliation = 256;
    internal static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan ReconciliationEntryTimeout = TimeSpan.FromMinutes(1);
    private readonly IReminderTable _reminderTable = reminderTable;
    private readonly IGrainFactory _grainFactory = grainFactory;
    private readonly ILogger<AdvancedReminderRecoveryGrain> _logger = logger;
    private readonly ReminderOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private bool _started;
    private bool _forceCurrentScan;
    private string? _scanContinuationToken;
    private DateTimeOffset _nextReconciliationUtc;

    public async Task StartAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        if (_started && now < _nextReconciliationUtc)
        {
            return;
        }

        if (force && !_started)
        {
            _forceCurrentScan = true;
        }

        var scanStartedUtc = now;
        await ReconcileAsync(force: _forceCurrentScan, cancellationToken);
        _started = true;
        _nextReconciliationUtc = scanStartedUtc.Add(ReconciliationPeriod);
    }

    internal async Task ReconcileAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var tasks = new List<Task>(BatchSize);
        for (var pagesScanned = 0; pagesScanned < MaxPagesPerReconciliation; pagesScanned++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Equal bounds traverse the full hash ring. The provider cursor keeps the scan
            // bounded by populated pages and lets the next heartbeat resume an incomplete pass.
            var page = await ReminderTableRead.WaitAsync(
                _reminderTable.ReadRows(0, 0, RecoveryPageSize, _scanContinuationToken), _timeProvider, cancellationToken);
            foreach (var entry in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!force
                    && !string.IsNullOrEmpty(entry.JobId)
                    && !string.IsNullOrEmpty(entry.JobShardId)
                    && !AdvancedReminderService.ShouldRecoverOverdueOccurrence(entry, now, _options))
                {
                    continue;
                }

                tasks.Add(ReconcileEntryAsync(entry, force, cancellationToken));
                await FlushTasksIfFullAsync(tasks);
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }

            _scanContinuationToken = page.ContinuationToken;
            if (_scanContinuationToken is null)
            {
                _forceCurrentScan = false;
                break;
            }
        }
    }

    private static async Task FlushTasksIfFullAsync(List<Task> tasks)
    {
        if (tasks.Count == BatchSize)
        {
            await Task.WhenAll(tasks);
            tasks.Clear();
        }
    }

    private async Task ReconcileEntryAsync(ReminderEntry entry, bool force, CancellationToken cancellationToken)
    {
        using var entryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var dispatcher = _grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(entry.GrainId.ToString());
            await dispatcher.EnsureScheduledAsync(
                    entry.GrainId,
                    entry.ReminderName,
                    entry.ScheduleId,
                    force,
                    entryCts.Token)
                .WaitAsync(ReconciliationEntryTimeout, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            entryCts.Cancel();
            _logger.LogError(
                exception,
                "Error reconciling advanced reminder {ReminderName} for grain {GrainId}.",
                entry.ReminderName,
                entry.GrainId);
        }
    }
}
