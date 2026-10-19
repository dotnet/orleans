namespace Benchmarks.Journaling;

/// <summary>
/// Reports the journal bytes which a durable task body's access records take, per access and per body, in each journal
/// format and for each kind of value: the entries which journal writes append to <c>$task-access-logs</c>.
/// </summary>
/// <remarks>Each entry is keyed by the task's identifier, <see cref="AccessRecordingFixture.TaskId"/>.</remarks>
internal static class AccessRecordingBytes
{
    public static void Run()
    {
        Console.WriteLine($"Task identifier: `{AccessRecordingFixture.TaskId}` ({AccessRecordingFixture.TaskId.ToString().Length} characters)");
        Console.WriteLine();
        Console.WriteLine("| Item | Format | Values | Bytes |");
        Console.WriteLine("|---|---|---|---:|");
        foreach (var format in AccessRecordingFixture.Formats)
        {
            foreach (var valueKind in Enum.GetValues<AccessRecordingValueKind>())
            {
                using var f = AccessRecordingFixture.Create(format, valueKind);
                var ops = AccessRecordingBenchmarks.Ops;
                var context = $"{format} | {valueKind}";
                Report(f, context, "A read, the attempt's only access", AccessRecordingScenario.ReadOnce, 1);
                Report(f, context, $"Each further read of another key (of {ops})", AccessRecordingScenario.ReadDistinct, ops, subtractSingle: true);
                Report(f, context, $"Each read of the same key of an unchanged dictionary (of {ops})", AccessRecordingScenario.ReadSameKey, ops);
                Report(f, context, $"Each read alternating between two keys of an unchanged dictionary (of {ops})", AccessRecordingScenario.ReadAlternating, ops);
                Report(f, context, $"{ops} reads of an unchanged value, in total", AccessRecordingScenario.ReadValue, 1);
                Report(f, context, $"Each iteration of read key A, write key B (of {ops})", AccessRecordingScenario.ReadAWriteB, ops);
                Report(f, context, $"Each result-less value write (of {ops})", AccessRecordingScenario.WriteValue, ops);
                Report(f, context, $"Each read of 64 keys of an unchanged dictionary (of {AccessRecordingBenchmarks.KeysReads})", AccessRecordingScenario.Keys, AccessRecordingBenchmarks.KeysReads);
                Report(f, context, "Typical body: 3 reads, 3 writes, 1 count", AccessRecordingScenario.TypicalBody, 1);
                Console.WriteLine($"| Removal at completion | {context} | {f.MeasureBytes(AccessRecordingScenario.TypicalBody).Removal} |");
                if (AccessRecordingFixture.Positions)
                {
                    Console.WriteLine($"| Barrier set and release, once per snapshot period in which executions hold positional reads | {context} | {f.MeasureBarrierBytes()} |");
                }
            }
        }
    }

    // Reports the record bytes of the scenario per access, of count; or, with subtractSingle, per access after the first.
    private static void Report(AccessRecordingFixture f, string context, string item, AccessRecordingScenario scenario, int count, bool subtractSingle = false)
    {
        double bytes = f.MeasureBytes(scenario).Records;
        if (subtractSingle)
        {
            bytes -= f.MeasureBytes(AccessRecordingScenario.ReadOnce).Records;
            bytes /= count - 1;
        }
        else
        {
            bytes /= count;
        }

        Console.WriteLine($"| {item} | {context} | {bytes:0.##} |");
    }
}
