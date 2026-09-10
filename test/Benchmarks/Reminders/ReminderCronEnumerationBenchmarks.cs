using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronEnumerationBenchmarks
{
    private readonly DateTime _start = new(2025, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private ReminderCronBuilder _builder = null!;

    [Params("* * * * * *", "*/15 * * * *")]
    public string Expression { get; set; } = null!;

    [Params("UTC", "America/New_York")]
    public string ZoneId { get; set; } = null!;

    [Params(10, 100, 1000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _builder = ReminderCronBuilder.FromExpression(Expression, TimeZoneInfo.FindSystemTimeZoneById(ZoneId));
        _ = StreamOccurrences();
    }

    [Benchmark]
    public long StreamOccurrences()
    {
        var remaining = Count;
        long lastTicks = 0;
        foreach (var occurrence in _builder.GetOccurrences(_start, _start.AddYears(1)))
        {
            lastTicks = occurrence.Ticks;
            if (--remaining == 0) break;
        }

        return lastTicks;
    }

    [Benchmark]
    public DateTime[] MaterializeOccurrences()
        => _builder.GetOccurrences(_start, _start.AddYears(1)).Take(Count).ToArray();
}
