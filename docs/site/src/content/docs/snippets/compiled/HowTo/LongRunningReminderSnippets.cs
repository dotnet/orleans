using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Documentation.HowTo.LongRunningReminder;

// <long_running_reminder_grain>
public interface ILongRunningReminderGrain : IGrainWithStringKey
{
    Task Start();
}

public sealed class LongRunningReminderGrain(
    ILogger<LongRunningReminderGrain> logger) :
    Grain,
    ILongRunningReminderGrain,
    IRemindable
{
    private const string ReminderName = "background-work";
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private Task? _backgroundTask;

    // <schedule_long_running_reminder>
    public async Task Start()
    {
        await this.RegisterOrUpdateReminder(
            ReminderName,
            dueTime: TimeSpan.Zero,
            period: TimeSpan.FromMinutes(1));
    }
    // </schedule_long_running_reminder>

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(
            reminderName,
            ReminderName,
            StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reminderName),
                reminderName,
                "The reminder name is not recognized.");
        }

        if (_backgroundTask is null or { IsCompleted: true })
        {
            logger.LogInformation(
                "Starting background work from reminder {ReminderName} at {TickTime}",
                reminderName,
                status.CurrentTickTime);

            _backgroundTask = RunBackgroundWork();
        }

        return Task.CompletedTask;
    }

    private async Task RunBackgroundWork()
    {
        await Task.CompletedTask.ConfigureAwait(
            ConfigureAwaitOptions.ContinueOnCapturedContext |
            ConfigureAwaitOptions.ForceYielding);

        try
        {
            while (!_shutdownCancellation.IsCancellationRequested)
            {
                await ProcessNextBatch(_shutdownCancellation.Token);
            }
        }
        catch (OperationCanceledException)
            when (_shutdownCancellation.IsCancellationRequested)
        {
            // Cancellation is the normal activation shutdown path.
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Background work stopped unexpectedly");
        }
    }

    private static Task ProcessNextBatch(
        CancellationToken cancellationToken)
    {
        return Task.Delay(
            TimeSpan.FromSeconds(1),
            cancellationToken);
    }

    public override async Task OnDeactivateAsync(
        DeactivationReason reason,
        CancellationToken cancellationToken)
    {
        _shutdownCancellation.Cancel();

        if (_backgroundTask is { IsCompleted: false } task)
        {
            await task.WaitAsync(cancellationToken);
        }

        _shutdownCancellation.Dispose();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }
}
// </long_running_reminder_grain>
