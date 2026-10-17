namespace Documentation.Diagnostics.DurableTasks;

public sealed class TrialGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0111
    // <orleans0111_violation>
    public async DurableTask ExpireTrialAsync(string customer)
    {
        // The wait lives in memory: a deactivation loses it, and the replay waits the full 14 days again.
        await Task.Delay(TimeSpan.FromDays(14));
        await Notify(customer, "Your trial has ended").WithId("expired");
    }
    // </orleans0111_violation>
#pragma warning restore ORLEANS0111

    // <orleans0111_fix_delay>
    public async DurableTask ExpireTrialDurablyAsync(string customer)
    {
        // The runtime records the due time, and Durable Jobs reactivates the grain when it is due.
        await DurableTask.Delay(TimeSpan.FromDays(14)).WithId("trial");
        await Notify(customer, "Your trial has ended").WithId("expired");
    }
    // </orleans0111_fix_delay>

    // <orleans0111_fix_delay_until>
    public async DurableTask RemindBeforeExpiryAsync(string customer, DateTimeOffset expiresAt)
    {
        await DurableTask.DelayUntil(expiresAt.AddDays(-1)).WithId("day-before");
        await Notify(customer, "Your trial ends tomorrow").WithId("reminder");
    }
    // </orleans0111_fix_delay_until>

    // <orleans0111_fix_timeout>
    public async DurableTask<bool> NotifyWithTimeoutAsync(string customer)
    {
        try
        {
            // Instead of racing the work with Task.Delay: a durable timeout which counts from the first run.
            await Notify(customer, "Your trial has ended").WaitAsync(TimeSpan.FromMinutes(5)).WithId("notify");
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
    // </orleans0111_fix_timeout>

    private async DurableTask Notify(string customer, string subject) =>
        await email.SendAsync(customer, subject, DurableTask.Current.CancellationToken);
}
