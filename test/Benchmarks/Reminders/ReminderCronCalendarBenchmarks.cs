using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronCalendarBenchmarks
{
    private readonly DateTime _start = new(2096, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private ReminderCronExpression _expression = null!;

    [Params("0 0 29 2 *", "0 0 29 2 MON", "0 0 31 2 *", "0 0 31 4 *",
        "0 9 1W * *", "0 9 L-3W * *", "0 9 ? * MON#5", "0 9 ? * FRIL")]
    public string Expression { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _expression = ReminderCronExpression.Parse(Expression);
        _ = _expression.GetNextOccurrence(_start);
    }

    [Benchmark]
    public DateTime? WarmSearch() => _expression.GetNextOccurrence(_start);

    // Include parsing explicitly so the impossible-date cache is empty on every call.
    [Benchmark]
    public DateTime? ParseAndSearch() => ReminderCronExpression.Parse(Expression).GetNextOccurrence(_start);
}
