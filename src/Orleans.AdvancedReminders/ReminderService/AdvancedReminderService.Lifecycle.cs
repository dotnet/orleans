using Microsoft.Extensions.Logging;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed partial class AdvancedReminderService
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(AdvancedReminderService),
            ServiceLifecycleStage.Active + 1,
            StartAsync,
            StopAsync);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.InitializationTimeout);
        try
        {
            await _reminderTable.StartAsync(timeout.Token).WaitAsync(timeout.Token);
            await _grainFactory.GetGrain<IAdvancedReminderRecoveryGrain>(0)
                .StartAsync(force: false, timeout.Token)
                .WaitAsync(timeout.Token);
            _recoveryMonitorTask ??= MonitorRecoveryAsync(_recoveryMonitorCts.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Advanced reminder initialization exceeded the configured timeout of {_options.InitializationTimeout}.",
                exception);
        }
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        _recoveryMonitorCts.Cancel();
        if (_recoveryMonitorTask is not null)
        {
            try
            {
                await _recoveryMonitorTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _recoveryMonitorCts.IsCancellationRequested)
            {
            }
        }

        await _reminderTable.StopAsync(cancellationToken);
    }

    private async Task MonitorRecoveryAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RecoveryHeartbeatPeriod, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await _grainFactory.GetGrain<IAdvancedReminderRecoveryGrain>(0)
                        .StartAsync(force: false, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error checking advanced reminder recovery service health.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
