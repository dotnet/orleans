using BenchmarkDotNet.Attributes;
using Orleans.AdvancedReminders;

namespace Benchmarks.Reminders;

[MemoryDiagnoser]
public class ReminderCronInvalidInputBenchmarks
{
    [Params("", " ", "@unknown", "* *", "60 * * * *", "1,,2 * * * *",
        "*/999999999999999999999 * * * *", "0 0 * * MON#6", "0 0 L-31 * *")]
    public string Expression { get; set; } = null!;

    [Benchmark]
    public bool TryParseInvalid() => ReminderCronExpression.TryParse(Expression, out _);
}
