using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronBenchmarks
{
    private readonly DateTime _start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private ReminderCronBuilder _builder = null!;
    private TimeZoneInfo _zone = null!;

    [Params("* * * * * *", "0 9 * * *", "0 0 29 2 *", "0 0 31 2 *")]
    public string Expression { get; set; } = null!;

    [Params("UTC", "America/New_York")]
    public string ZoneId { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _zone = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);
        _builder = ReminderCronBuilder.FromExpression(Expression, _zone);
        _ = _builder.GetNextOccurrence(_start);
    }

    [Benchmark]
    public ReminderCronExpression Parse() => ReminderCronExpression.Parse(Expression);

    [Benchmark]
    public DateTime? NextOccurrence() => _builder.GetNextOccurrence(_start);

    [Benchmark]
    public DateTime? FirstOccurrence() => ReminderCronBuilder.FromExpression(Expression, _zone).GetNextOccurrence(_start);

    [Benchmark]
    public int EnumerateOccurrences()
        => _builder.GetOccurrences(_start, _start.AddYears(410)).Take(100).Count();
}
