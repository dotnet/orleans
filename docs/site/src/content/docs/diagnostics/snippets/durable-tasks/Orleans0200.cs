using Orleans.Runtime;

namespace Documentation.Diagnostics.DurableTasks;

public sealed class ReminderGrain : DurableGrain
{
    private readonly IEmailSender _email;
    private readonly IPersistentState<int> _remindersSent;
    private readonly IPersistentState<OrderStatus> _status;

    public ReminderGrain(IEmailSender email)
    {
        _email = email;
        _remindersSent = StateManager.GetOrAddPersistentState<int>("reminders-sent");
        _status = StateManager.GetOrAddPersistentState<OrderStatus>("status");
    }

#pragma warning disable ORLEANS0200
    // <orleans0200_violation>
    public async DurableTask<int> RemindAsync(string recipient)
    {
        // A replay runs this line again after the reminder was sent, and counts it twice.
        _remindersSent.State++;
        await SendReminderAsync(recipient).WithId("send");
        return _remindersSent.State;
    }

    public async DurableTask ShipWhenPaidAsync()
    {
        // A replay reads the status which the "ship" step wrote, takes the other branch, and skips the recorded step.
        if (_status.State == OrderStatus.Paid)
        {
            await MarkShipped().WithId("ship");
        }
    }
    // </orleans0200_violation>
#pragma warning restore ORLEANS0200

    // <orleans0200_fix_once>
    public async DurableTask<int> RemindOnceAsync(string recipient)
    {
        // The effect runs once, and its change commits with its record. A replay returns the recorded count.
        var sent = DurableTask.RunInline("count", () => ++_remindersSent.State);
        await SendReminderAsync(recipient).WithId("send");
        return sent;
    }

    public async DurableTask ShipWhenPaidOnceAsync()
    {
        // A replay returns the status which the first run read, so it takes the same branch.
        var status = DurableTask.RunInline("read-status", () => _status.State);
        if (status == OrderStatus.Paid)
        {
            await MarkShipped().WithId("ship");
        }
    }
    // </orleans0200_fix_once>

    // <orleans0200_fix_tail>
    public async DurableTask<int> RemindThenCountAsync(string recipient)
    {
        await SendReminderAsync(recipient).WithId("send");

        // No durable operation follows, so this change commits with the task's result and no replay runs it again.
        return ++_remindersSent.State;
    }
    // </orleans0200_fix_tail>

    // <orleans0200_fix_step>
    // A local step: its whole body is its final code, so its change commits with its recorded result.
    private async DurableTask<OrderStatus> MarkShipped() => _status.State = OrderStatus.Shipped;
    // </orleans0200_fix_step>

    private async DurableTask SendReminderAsync(string recipient) =>
        await _email.SendAsync(recipient, "Your order is waiting", DurableTask.Current.CancellationToken);
}

// <orleans0200_journaled>
public sealed class JournaledReminderGrain : DurableGrain
{
    private readonly IEmailSender _email;
    private readonly IDurableValue<int> _remindersSent;
    private readonly IDurableValue<OrderStatus> _status;

    public JournaledReminderGrain(IEmailSender email)
    {
        _email = email;
        _remindersSent = StateManager.GetOrAddValue<int>("reminders-sent");
        _status = StateManager.GetOrAddValue<OrderStatus>("status");
    }

    public async DurableTask<int> RemindAsync(string recipient)
    {
        // The runtime records the read and the write, so a replay returns the recorded count and doesn't count again.
        _remindersSent.Value++;
        await DurableTask.Run(ct => _email.SendAsync(recipient, "Your order is waiting", ct)).WithId("send");
        return _remindersSent.Value;
    }

    public async DurableTask ShipWhenPaidAsync(string recipient)
    {
        // A replay returns the status which the first run read, so it takes the same branch, and the recorded write
        // isn't applied again.
        if (_status.Value == OrderStatus.Paid)
        {
            _status.Value = OrderStatus.Shipped;
            await DurableTask.Run(ct => _email.SendAsync(recipient, "Your order has shipped", ct)).WithId("notify");
        }
    }
}
// </orleans0200_journaled>