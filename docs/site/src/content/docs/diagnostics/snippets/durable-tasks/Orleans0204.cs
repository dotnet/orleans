namespace Documentation.Diagnostics.DurableTasks;

public sealed class TicketGrain : DurableGrain
{
    private readonly IDurableValue<int> _lastNumber;

    public TicketGrain() => _lastNumber = StateManager.GetOrAddValue<int>("last-number");

#pragma warning disable ORLEANS0204
    // <orleans0204_violation>
    public async DurableTask<string> IssueAsync()
    {
        // The grain records the increment, so a replay returns the recorded number.
        var number = ++_lastNumber.Value;
        var reference = Guid.Empty;
        var issuedAt = DateTimeOffset.MinValue;
        DurableTask.RunInline("allocate", () =>
        {
            // A replay returns the recorded result without running the effect, so these assignments don't happen.
            reference = Guid.NewGuid();
            issuedAt = DateTimeOffset.UtcNow;
        });

        await Print(number, reference).WithId("print");
        return $"{number}-{reference:N}-{issuedAt:yyyyMMdd}";
    }
    // </orleans0204_violation>
#pragma warning restore ORLEANS0204

    // <orleans0204_fix>
    public async DurableTask<string> IssueOnceAsync()
    {
        var number = ++_lastNumber.Value;

        // The effect returns its outputs, so every replay receives the recorded values.
        var (reference, issuedAt) = DurableTask.RunInline("allocate", () => (Guid.NewGuid(), DateTimeOffset.UtcNow));

        await Print(number, reference).WithId("print");
        return $"{number}-{reference:N}-{issuedAt:yyyyMMdd}";
    }
    // </orleans0204_fix>

    // <orleans0204_fix_record>
    [GenerateSerializer, Immutable]
    public sealed record Allocation(int Number, Guid Reference, DateTimeOffset IssuedAt);

    public async DurableTask<Allocation> IssueAllocationAsync()
    {
        var number = ++_lastNumber.Value;
        var allocation = DurableTask.RunInline(
            "allocate",
            () => new Allocation(number, Guid.NewGuid(), DateTimeOffset.UtcNow));

        await Print(allocation.Number, allocation.Reference).WithId("print");
        return allocation;
    }
    // </orleans0204_fix_record>

    private static async DurableTask<bool> Print(int number, Guid reference) => number > 0 && reference != Guid.Empty;
}
