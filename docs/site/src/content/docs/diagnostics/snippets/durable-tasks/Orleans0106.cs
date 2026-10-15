namespace Documentation.Diagnostics.DurableTasks;

public sealed class ReminderScheduleGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0106
    // <orleans0106_violation>
    public async DurableTask RemindAsync(string customer, int reminders)
    {
        var remind = SendReminder(customer);
        for (var i = 0; i < reminders; i++)
        {
            await DurableTask.Delay(TimeSpan.FromDays(1)).WithId($"wait-{i}");

            // The definition ran in the first iteration, so the second iteration throws.
            await remind.WithId($"remind-{i}");
        }
    }
    // </orleans0106_violation>
#pragma warning restore ORLEANS0106

    // <orleans0106_fix>
    public async DurableTask RemindEachDayAsync(string customer, int reminders)
    {
        for (var i = 0; i < reminders; i++)
        {
            await DurableTask.Delay(TimeSpan.FromDays(1)).WithId($"wait-{i}");

            // Each call creates a new definition for this iteration's run.
            await SendReminder(customer).WithId($"remind-{i}");
        }
    }
    // </orleans0106_fix>

    // <orleans0106_fix_factory>
    public async DurableTask RemindTwiceAsync(string customer)
    {
        // A factory creates a definition for each run.
        DurableTask Remind() => SendReminder(customer);

        await Remind().WithId("first-reminder");
        await DurableTask.Delay(TimeSpan.FromDays(3)).WithId("grace");
        await Remind().WithId("final-reminder");
    }
    // </orleans0106_fix_factory>

    private async DurableTask SendReminder(string customer) =>
        await email.SendAsync(customer, "Your cart is waiting", DurableTask.Current.CancellationToken);
}
