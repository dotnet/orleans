namespace Documentation.Diagnostics.DurableTasks;

// <orleans0028_step>
public sealed class CounterGrain : DurableGrain
{
    private readonly IDurableValue<int> _count;

    public CounterGrain() => _count = StateManager.GetOrAddValue<int>("count");

    // A step without an await: the state change and the step's result commit in one journal write.
    public async DurableTask<int> IncrementAsync() => ++_count.Value;

    public async DurableTask<int> IncrementTwiceAsync()
    {
        await IncrementAsync().WithId("first");
        return await IncrementAsync().WithId("second");
    }
}
// </orleans0028_step>
