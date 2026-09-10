using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronParsingBenchmarks
{
    [Params(
        "* * * * * *",
        "@daily",
        "@yearly",
        "0 9 * * MON-FRI",
        "5-45/10 0,15,30,45 8-18 ? JAN,MAR MON-FRI",
        "0 23-2 * * FRI-MON",
        "0 9 LW * *",
        "0 9 L-3W * *",
        "0 9 ? * MON#5",
        "0 9 ? * FRIL",
        "  0\t9  *  *  mon-fri  ")]
    public string Expression { get; set; } = null!;

    [Benchmark]
    public ReminderCronExpression Parse() => ReminderCronExpression.Parse(Expression);

    [Benchmark]
    public bool TryParse() => ReminderCronExpression.TryParse(Expression, out _);
}
