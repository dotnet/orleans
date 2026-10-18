namespace Documentation.Diagnostics.DurableTasks;

public sealed class ReminderGrain : DurableGrain
{
    private readonly IEmailSender _email;
    private readonly IDurableValue<int> _remindersSent;
    private readonly IDurableValue<OrderStatus> _status;

    public ReminderGrain(IEmailSender email)
    {
        _email = email;
        _remindersSent = StateManager.GetOrAddValue<int>("reminders-sent");
        _status = StateManager.GetOrAddValue<OrderStatus>("status");
    }

#pragma warning disable ORLEANS0100
    // <orleans0100_violation>
    public async DurableTask<int> RemindAsync(string recipient)
    {
        // A replay runs this line again after the reminder was sent, and counts it twice.
        _remindersSent.Value++;
        await SendReminderAsync(recipient).WithId("send");
        return _remindersSent.Value;
    }

    public async DurableTask ShipWhenPaidAsync()
    {
        // A replay reads the status which the "ship" step wrote, takes the other branch, and skips the recorded step.
        if (_status.Value == OrderStatus.Paid)
        {
            await MarkShipped().WithId("ship");
        }
    }
    // </orleans0100_violation>
#pragma warning restore ORLEANS0100

    // <orleans0100_fix_once>
    public async DurableTask<int> RemindOnceAsync(string recipient)
    {
        // The effect runs once, and its change commits with its record. A replay returns the recorded count.
        var sent = DurableTask.RunInline("count", () => ++_remindersSent.Value);
        await SendReminderAsync(recipient).WithId("send");
        return sent;
    }

    public async DurableTask ShipWhenPaidOnceAsync()
    {
        // A replay returns the status which the first run read, so it takes the same branch.
        var status = DurableTask.RunInline("read-status", () => _status.Value);
        if (status == OrderStatus.Paid)
        {
            await MarkShipped().WithId("ship");
        }
    }
    // </orleans0100_fix_once>

    // <orleans0100_fix_tail>
    public async DurableTask<int> RemindThenCountAsync(string recipient)
    {
        await SendReminderAsync(recipient).WithId("send");

        // No durable operation follows, so this change commits with the task's result and no replay runs it again.
        return ++_remindersSent.Value;
    }
    // </orleans0100_fix_tail>

    // <orleans0100_fix_step>
    // A local step: its whole body is its final code, so its change commits with its recorded result.
    private async DurableTask<OrderStatus> MarkShipped() => _status.Value = OrderStatus.Shipped;
    // </orleans0100_fix_step>

    private async DurableTask SendReminderAsync(string recipient) =>
        await _email.SendAsync(recipient, "Your order is waiting", DurableTask.Current.CancellationToken);
}
