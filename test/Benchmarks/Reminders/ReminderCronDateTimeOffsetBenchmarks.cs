using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronDateTimeOffsetBenchmarks
{
    private ReminderCronBuilder _builder = null!;
    private DateTimeOffset _start;

    [Params("UTC", "America/New_York")]
    public string ZoneId { get; set; } = null!;

    [Params(-570, 0, 345)]
    public int OffsetMinutes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _start = new DateTimeOffset(2025, 11, 2, 5, 0, 0, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(OffsetMinutes));
        _builder = ReminderCronBuilder.EveryMinutes(15).InTimeZone(ZoneId);
        _ = _builder.GetNextOccurrence(_start);
    }

    [Benchmark]
    public DateTimeOffset? NextOccurrence() => _builder.GetNextOccurrence(_start);

    [Benchmark]
    public DateTimeOffset[] EnumerateOneHundred()
        => _builder.GetOccurrences(_start, _start.AddDays(3)).Take(100).ToArray();
}
