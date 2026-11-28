using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronTimeZoneBenchmarks
{
    private ReminderCronBuilder _builder = null!;
    private DateTime _start;
    private int _day;

    [Params("30 1 * * *", "30 2 * * *", "*/15 * * * *")]
    public string Expression { get; set; } = null!;

    [Params("America/New_York", "Europe/Paris", "Europe/Vienna", "Europe/Kyiv",
        "Australia/Sydney", "Australia/Lord_Howe", "Asia/Dubai", "Asia/Kathmandu",
        "Pacific/Chatham", "Africa/Casablanca", "Pacific/Apia")]
    public string ZoneId { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        _start = ZoneId switch
        {
            "America/New_York" => new DateTime(2025, 11, 2, 5, 0, 0, DateTimeKind.Utc),
            "Europe/Paris" or "Europe/Vienna" or "Europe/Kyiv" => new DateTime(2025, 10, 26, 0, 0, 0, DateTimeKind.Utc),
            "Australia/Sydney" => new DateTime(2025, 4, 5, 15, 0, 0, DateTimeKind.Utc),
            "Asia/Dubai" => new DateTime(2025, 11, 1, 19, 0, 0, DateTimeKind.Utc),
            "Asia/Kathmandu" => new DateTime(2025, 11, 1, 18, 0, 0, DateTimeKind.Utc),
            "Pacific/Chatham" => new DateTime(2025, 4, 5, 13, 0, 0, DateTimeKind.Utc),
            "Australia/Lord_Howe" => new DateTime(2025, 4, 5, 14, 0, 0, DateTimeKind.Utc),
            "Africa/Casablanca" => new DateTime(2025, 2, 23, 1, 0, 0, DateTimeKind.Utc),
            "Pacific/Apia" => new DateTime(2011, 12, 30, 9, 0, 0, DateTimeKind.Utc),
            _ => throw new InvalidOperationException(ZoneId),
        };
        _builder = ReminderCronBuilder.FromExpression(Expression, TimeZoneInfo.FindSystemTimeZoneById(ZoneId));
        _ = _builder.GetNextOccurrence(_start);
    }

    [Benchmark]
    public DateTime? WarmedOccurrence() => _builder.GetNextOccurrence(_start);

    [Benchmark]
    public DateTime? UncachedDate()
    {
        _day = (_day + 1) % 4096;
        return _builder.GetNextOccurrence(_start.AddDays(_day));
    }
}
