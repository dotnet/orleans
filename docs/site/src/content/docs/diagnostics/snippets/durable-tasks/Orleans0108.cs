namespace Documentation.Diagnostics.DurableTasks;

public sealed class OnboardingGrain(IEmailSender email) : DurableGrain
{
#pragma warning disable ORLEANS0108
    // <orleans0108_violation>
    public async DurableTask OnboardAsync(string customer)
    {
        await Send(customer, "Welcome").WithId("email");
        await DurableTask.Delay(TimeSpan.FromDays(7)).WithId("first-week");

        // Recorded as "email$2". After a deployment removes the welcome email, a replay identifies this call as "email",
        // which holds the welcome email's record.
        await Send(customer, "Tips for your first week").WithId("email");
    }
    // </orleans0108_violation>
#pragma warning restore ORLEANS0108

    // <orleans0108_fix>
    public async DurableTask OnboardWithNamesAsync(string customer)
    {
        await Send(customer, "Welcome").WithId("welcome-email");
        await DurableTask.Delay(TimeSpan.FromDays(7)).WithId("first-week");
        await Send(customer, "Tips for your first week").WithId("tips-email");
    }
    // </orleans0108_fix>

    // <orleans0108_fix_loop>
    public async DurableTask SendSeriesAsync(string customer, string[] subjects)
    {
        // One call site which a loop repeats is named once: its occurrences are "lesson", "lesson$2", and so on. A name
        // derived from each input also identifies it.
        foreach (var subject in subjects)
        {
            await Send(customer, subject).WithId("lesson");
        }
    }
    // </orleans0108_fix_loop>

    private async DurableTask Send(string customer, string subject) =>
        await email.SendAsync(customer, subject, DurableTask.Current.CancellationToken);
}
