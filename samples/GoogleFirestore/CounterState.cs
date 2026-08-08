namespace GoogleFirestore;

[GenerateSerializer]
public sealed class CounterState
{
    [Id(0)]
    public int Value { get; set; }

    [Id(1)]
    public int ReminderTicks { get; set; }

    [Id(2)]
    public DateTime LastUpdatedUtc { get; set; }
}
