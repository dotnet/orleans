namespace Documentation.Diagnostics.DurableTasks;

public sealed class TicketGrain : DurableGrain
{
    private readonly IDurableValue<int> _lastNumber;

    public TicketGrain() => _lastNumber = StateManager.GetOrAddValue<int>("last-number");

#pragma warning disable ORLEANS0104
    // <orleans0104_violation>
    public async DurableTask<string> IssueAsync()
    {
        var number = 0;
        var reference = Guid.Empty;
        DurableTask.RunInline("allocate", () =>
        {
            // A replay returns the recorded result without running the effect, so these assignments don't happen.
            number = ++_lastNumber.Value;
            reference = Guid.NewGuid();
        });

        await Print(number, reference).WithId("print");
        return $"{number}-{reference:N}";
    }
    // </orleans0104_violation>
#pragma warning restore ORLEANS0104

    // <orleans0104_fix>
    public async DurableTask<string> IssueOnceAsync()
    {
        // The effect returns its outputs, so every replay receives the recorded values.
        var (number, reference) = DurableTask.RunInline("allocate", () => (++_lastNumber.Value, Guid.NewGuid()));

        await Print(number, reference).WithId("print");
        return $"{number}-{reference:N}";
    }
    // </orleans0104_fix>

    // <orleans0104_fix_record>
    [GenerateSerializer, Immutable]
    public sealed record Allocation(int Number, Guid Reference, DateTimeOffset IssuedAt);

    public async DurableTask<Allocation> IssueAllocationAsync()
    {
        var allocation = DurableTask.RunInline(
            "allocate",
            () => new Allocation(++_lastNumber.Value, Guid.NewGuid(), DateTimeOffset.UtcNow));

        await Print(allocation.Number, allocation.Reference).WithId("print");
        return allocation;
    }
    // </orleans0104_fix_record>

    private static async DurableTask<bool> Print(int number, Guid reference) => number > 0 && reference != Guid.Empty;
}
