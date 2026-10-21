namespace Documentation.Diagnostics.DurableTasks;

public sealed class LoyaltyGrain : DurableGrain
{
    private readonly IDurableValue<int> _points;

    public LoyaltyGrain() => _points = StateManager.GetOrAddValue<int>("points");

#pragma warning disable ORLEANS0220
    // <orleans0220_violation>
    public async DurableTask<int> AwardAsync(string customer, bool firstOrder)
    {
        if (firstOrder)
        {
            // $child-1, in the executions of first orders only.
            await SendWelcome(customer);
        }

        // $child-1 or $child-2, and the statement $child-2 or $child-3, depending on the condition.
        var points = DurableTask.RunInline(() => _points.Value += 100);
        await SendStatement(customer, points);
        return points;
    }
    // </orleans0220_violation>
#pragma warning restore ORLEANS0220

    // <orleans0220_fix>
    public async DurableTask<int> AwardNamedAsync(string customer, bool firstOrder)
    {
        if (firstOrder)
        {
            await SendWelcome(customer).WithId("send-welcome");
        }

        // Each name identifies its call site, whichever operations ran before it.
        var points = DurableTask.RunInline("add-points", () => _points.Value += 100);
        await SendStatement(customer, points).WithId("send-statement");
        return points;
    }
    // </orleans0220_fix>

    private static async DurableTask<bool> SendWelcome(string customer) => customer.Length > 0;

    private static async DurableTask<bool> SendStatement(string customer, int points) => customer.Length > 0 && points > 0;
}
